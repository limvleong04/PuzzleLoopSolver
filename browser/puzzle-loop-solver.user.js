// ==UserScript==
// @name         Puzzle Loop Solver
// @namespace    puzzle-loop-solver
// @version      1.1.0
// @description  Slitherlink solver for puzzle-loop.com. Show the answer over the board, or have it drawn in. Reads the puzzle from the page and checks the answer against the page's own solution hash.
// @match        https://www.puzzle-loop.com/*
// @match        https://puzzle-loop.com/*
// @grant        none
// @run-at       document-idle
// ==/UserScript==

// One file, three uses:
//   1. Tampermonkey / Violentmonkey userscript (adds a small Show / Draw panel to the page).
//   2. Bookmarklet (built from this file by make-bookmarklet.js).
//   3. Node module (require('./puzzle-loop-solver.user.js')) for the command line and the tests.
// The solver itself is plain logic: no screenshots, no network, no AI.

(function () {
  'use strict';

  const UNK = 0, LINE = 1, CROSS = 2;

  // ---------------------------------------------------------------------------
  // Task format, as the site writes it: row by row, a digit is one clue,
  // a letter skips that many empty cells (a = 1 ... z = 26).
  // ---------------------------------------------------------------------------
  function decodeTask(task, width, height) {
    const clues = new Int8Array(width * height).fill(-1);
    let i = 0;
    for (const ch of task) {
      if (ch >= '0' && ch <= '9') {
        if (i < clues.length) clues[i] = ch.charCodeAt(0) - 48;
        i++;
      } else if (ch >= 'a' && ch <= 'z') {
        i += ch.charCodeAt(0) - 96;
      }
    }
    if (i !== width * height) {
      throw new Error('The task describes ' + i + ' cells but the board is ' + width + ' x ' + height + ' = ' + (width * height) + '.');
    }
    return clues;
  }

  // ---------------------------------------------------------------------------
  // Edge numbering matches the site's answer string exactly:
  //   horizontal edges first, row 0..H, column 0..W-1  -> index r*W + c
  //   then vertical edges,   row 0..H-1, column 0..W   -> index HE + r*(W+1) + c
  // so the answer is just every edge in index order, 'y' for a line and 'n' otherwise.
  // ---------------------------------------------------------------------------
  function createSolver(width, height, clues, options) {
    const W = width, H = height;
    const NC = W * H, NV = (W + 1) * (H + 1);
    const HE = (H + 1) * W, E = HE + H * (W + 1);
    const maxBranches = (options && options.maxBranches) || 200000;

    const eA = new Int32Array(E), eB = new Int32Array(E);           // end dots
    const eC1 = new Int32Array(E).fill(-1), eC2 = new Int32Array(E).fill(-1); // cells either side
    const cellE = new Int32Array(NC * 4);
    const vE = new Int32Array(NV * 4).fill(-1), vDeg = new Int8Array(NV);
    const dot = (r, c) => r * (W + 1) + c;

    for (let r = 0; r <= H; r++) {
      for (let c = 0; c < W; c++) {
        const e = r * W + c;
        eA[e] = dot(r, c); eB[e] = dot(r, c + 1);
        if (r > 0) eC1[e] = (r - 1) * W + c;
        if (r < H) eC2[e] = r * W + c;
      }
    }
    for (let r = 0; r < H; r++) {
      for (let c = 0; c <= W; c++) {
        const e = HE + r * (W + 1) + c;
        eA[e] = dot(r, c); eB[e] = dot(r + 1, c);
        if (c > 0) eC1[e] = r * W + c - 1;
        if (c < W) eC2[e] = r * W + c;
      }
    }
    for (let r = 0; r < H; r++) {
      for (let c = 0; c < W; c++) {
        const i = (r * W + c) * 4;
        cellE[i] = r * W + c;
        cellE[i + 1] = (r + 1) * W + c;
        cellE[i + 2] = HE + r * (W + 1) + c;
        cellE[i + 3] = HE + r * (W + 1) + c + 1;
      }
    }
    for (let e = 0; e < E; e++) {
      vE[eA[e] * 4 + vDeg[eA[e]]++] = e;
      vE[eB[e] * 4 + vDeg[eB[e]]++] = e;
    }

    // ---- state ----
    const val = new Int8Array(E);
    const cLine = new Int8Array(NC), cCross = new Int8Array(NC);
    const vLine = new Int8Array(NV), vCross = new Int8Array(NV);
    // For a dot at the end of a line segment: the dot at the segment's other end, and the segment's length.
    const mate = new Int32Array(NV), plen = new Int32Array(NV);
    let totalLines = 0, unknown = E, closed = 0, unsat = 0;
    for (let c = 0; c < NC; c++) if (clues[c] > 0) unsat++;

    // Undo trail: edges in assignment order, plus saved mate/length entries (3 ints each).
    const trail = new Int32Array(E), trailM = new Int32Array(E);
    let tl = 0;
    const mt = new Int32Array(E * 6 + 6);
    let ml = 0;

    // Work queue of cells (0..NC-1) and dots (NC..NC+NV-1) to re-examine.
    const QN = NC + NV;
    const q = new Int32Array(QN), inQ = new Uint8Array(QN);
    let qh = 0, qt = 0, qn = 0;
    function push(i) { if (!inQ[i]) { inQ[i] = 1; q[qt] = i; qt = qt + 1 === QN ? 0 : qt + 1; qn++; } }
    function clearQueue() { while (qn) { inQ[q[qh]] = 0; qh = qh + 1 === QN ? 0 : qh + 1; qn--; } qh = qt = 0; }

    const stats = { assignments: 0, probes: 0, branches: 0, passes: 0 };

    function lineInCell(c) {
      const k = clues[c], n = ++cLine[c];
      if (k < 0) return true;
      if (n - 1 === k) unsat++; else if (n === k) unsat--;
      return n <= k;
    }
    function lineOutOfCell(c) {
      const k = clues[c], n = cLine[c]--;
      if (k < 0) return;
      if (n === k) unsat++; else if (n - 1 === k) unsat--;
    }

    // Set one edge. Always records the change (so undo works); returns false on a contradiction.
    function assign(e, x) {
      stats.assignments++;
      val[e] = x; trail[tl] = e; trailM[tl] = ml; tl++; unknown--;
      const a = eA[e], b = eB[e], c1 = eC1[e], c2 = eC2[e];
      let ok = true;
      if (x === LINE) {
        if (closed) ok = false; // a finished loop can't take another line
        totalLines++;
        if (c1 >= 0 && !lineInCell(c1)) ok = false;
        if (c2 >= 0 && !lineInCell(c2)) ok = false;
        const la = ++vLine[a], lb = ++vLine[b];
        if (la > 2 || lb > 2) {
          ok = false;
        } else if (la === 2 && lb === 2 && mate[a] === b) {
          // This edge joins the two ends of one segment: the loop closes.
          // Only allowed if that loop is every line on the board and every clue is met.
          if (totalLines !== plen[a] + 1 || unsat !== 0) ok = false;
          mt[ml++] = -1; mt[ml++] = closed; mt[ml++] = 0;
          closed = 1;
        } else {
          const ea = la === 1 ? a : mate[a];
          const eb = lb === 1 ? b : mate[b];
          const len = (la === 1 ? 0 : plen[a]) + (lb === 1 ? 0 : plen[b]) + 1;
          mt[ml++] = ea; mt[ml++] = mate[ea]; mt[ml++] = plen[ea];
          mt[ml++] = eb; mt[ml++] = mate[eb]; mt[ml++] = plen[eb];
          mate[ea] = eb; mate[eb] = ea; plen[ea] = len; plen[eb] = len;
          push(NC + ea); push(NC + eb);
        }
      } else {
        if (c1 >= 0) { cCross[c1]++; if (clues[c1] > 4 - cCross[c1]) ok = false; }
        if (c2 >= 0) { cCross[c2]++; if (clues[c2] > 4 - cCross[c2]) ok = false; }
        vCross[a]++; vCross[b]++;
      }
      if (c1 >= 0) push(c1);
      if (c2 >= 0) push(c2);
      push(NC + a); push(NC + b);
      return ok;
    }

    function undo(t) {
      while (tl > t) {
        tl--;
        const e = trail[tl], x = val[e];
        val[e] = UNK; unknown++;
        const a = eA[e], b = eB[e], c1 = eC1[e], c2 = eC2[e];
        if (x === LINE) {
          totalLines--;
          if (c1 >= 0) lineOutOfCell(c1);
          if (c2 >= 0) lineOutOfCell(c2);
          vLine[a]--; vLine[b]--;
        } else {
          if (c1 >= 0) cCross[c1]--;
          if (c2 >= 0) cCross[c2]--;
          vCross[a]--; vCross[b]--;
        }
        const m0 = trailM[tl];
        while (ml > m0) {
          ml -= 3;
          const v = mt[ml];
          if (v < 0) closed = mt[ml + 1];
          else { mate[v] = mt[ml + 1]; plen[v] = mt[ml + 2]; }
        }
      }
      clearQueue();
    }

    // Would adding edge e (whose end dot v is a segment end) close a valid, complete loop?
    function closureWouldBeValid(e, v) {
      if (closed || totalLines !== plen[v]) return false; // other lines exist outside this segment
      let d = unsat;
      const c1 = eC1[e], c2 = eC2[e];
      if (c1 >= 0 && clues[c1] >= 0) { const n = cLine[c1], k = clues[c1]; if (n === k) d++; else if (n + 1 === k) d--; }
      if (c2 >= 0 && clues[c2] >= 0) { const n = cLine[c2], k = clues[c2]; if (n === k) d++; else if (n + 1 === k) d--; }
      return d === 0;
    }

    // Apply the simple rules until nothing changes. Returns false on a contradiction.
    function propagate() {
      for (;;) {
        while (qn) {
          const i = q[qh]; qh = qh + 1 === QN ? 0 : qh + 1; qn--; inQ[i] = 0;
          if (i < NC) {
            // Cell: number of lines around it must equal the clue.
            const k = clues[i];
            if (k < 0) continue;
            const l = cLine[i], u = 4 - l - cCross[i];
            if (l > k || l + u < k) return false;
            if (u === 0) continue;
            const want = l === k ? CROSS : (l + u === k ? LINE : UNK);
            if (want === UNK) continue;
            for (let j = i * 4, end = j + 4; j < end; j++) {
              const e = cellE[j];
              if (val[e] === UNK && !assign(e, want)) return false;
            }
          } else {
            // Dot: 0 or 2 lines, never 1 or 3+. No loop may close early.
            const v = i - NC;
            const l = vLine[v], u = vDeg[v] - l - vCross[v];
            if (l > 2 || (l === 1 && u === 0)) return false;
            if (u === 0) continue;
            const base = v * 4, deg = vDeg[v];
            if (l === 2 || (l === 0 && u === 1)) {
              for (let j = 0; j < deg; j++) { const e = vE[base + j]; if (val[e] === UNK && !assign(e, CROSS)) return false; }
            } else if (l === 1) {
              if (u === 1) {
                for (let j = 0; j < deg; j++) { const e = vE[base + j]; if (val[e] === UNK && !assign(e, LINE)) return false; }
              } else {
                const m = mate[v];
                for (let j = 0; j < deg; j++) {
                  const e = vE[base + j];
                  if (val[e] !== UNK) continue;
                  const other = eA[e] === v ? eB[e] : eA[e];
                  if (other === m && !closureWouldBeValid(e, v)) { if (!assign(e, CROSS)) return false; }
                }
              }
            }
          }
        }
        // Once the loop is closed every remaining edge is empty.
        if (closed && unknown > 0) {
          for (let e = 0; e < E; e++) if (val[e] === UNK && !assign(e, CROSS)) return false;
          continue;
        }
        return true;
      }
    }

    // Trial: for each open edge, try "line" and "no line". If one side contradicts, the other is forced.
    // If both sides agree on some other edge, that edge is forced too.
    const stamp = new Int32Array(E), sval = new Int8Array(E);
    const common = new Int32Array(E), commonVal = new Int8Array(E);
    let curStamp = 0;

    function probePass() {
      stats.passes++;
      let progress = false;
      for (let e = 0; e < E; e++) {
        if (val[e] !== UNK) continue;
        stats.probes++;
        const t = tl;
        const okL = assign(e, LINE) && propagate();
        if (okL) { curStamp++; for (let i = t + 1; i < tl; i++) { const f = trail[i]; stamp[f] = curStamp; sval[f] = val[f]; } }
        undo(t);
        if (!okL) {
          if (!(assign(e, CROSS) && propagate())) return -1;
          progress = true;
          continue;
        }
        const okX = assign(e, CROSS) && propagate();
        let nc = 0;
        if (okX) {
          for (let i = t + 1; i < tl; i++) {
            const f = trail[i];
            if (stamp[f] === curStamp && sval[f] === val[f]) { common[nc] = f; commonVal[nc] = val[f]; nc++; }
          }
        }
        undo(t);
        if (!okX) {
          if (!(assign(e, LINE) && propagate())) return -1;
          progress = true;
          continue;
        }
        if (nc) {
          for (let i = 0; i < nc; i++) {
            const f = common[i];
            if (val[f] === UNK && !assign(f, commonVal[i])) return -1;
          }
          if (!propagate()) return -1;
          progress = true;
        }
      }
      return progress ? 1 : 0;
    }

    function pickBranchEdge() {
      // Extend a segment end first: it is the most constrained choice.
      for (let v = 0; v < NV; v++) {
        if (vLine[v] !== 1) continue;
        for (let j = 0; j < vDeg[v]; j++) { const e = vE[v * 4 + j]; if (val[e] === UNK) return e; }
      }
      for (let e = 0; e < E; e++) if (val[e] === UNK) return e;
      return -1;
    }

    let aborted = false;
    function search() {
      if (!propagate()) return false;
      while (unknown > 0) {
        const r = probePass();
        if (r < 0) return false;
        if (r === 0) break;
      }
      if (unknown === 0) return closed === 1;
      if (++stats.branches > maxBranches) { aborted = true; return false; }
      const e = pickBranchEdge();
      const t = tl;
      if (assign(e, LINE) && search()) return true;
      undo(t);
      if (aborted) return false;
      if (assign(e, CROSS) && search()) return true;
      undo(t);
      return false;
    }

    function solve() {
      for (let c = 0; c < NC; c++) push(c);
      for (let v = 0; v < NV; v++) push(NC + v);
      const ok = search();
      return { ok, aborted };
    }

    return { solve, val, stats, E, HE, W, H };
  }

  // Solve a task string. Returns { ok, answer, edges, stats, ms, message }.
  function solveTask(task, width, height, options) {
    const t0 = Date.now();
    const clues = decodeTask(task, width, height);
    const s = createSolver(width, height, clues, options);
    const r = s.solve();
    const ms = Date.now() - t0;
    if (!r.ok) {
      return {
        ok: false, ms, stats: s.stats,
        message: r.aborted ? 'Gave up after ' + s.stats.branches + ' guesses.' : 'This puzzle has no solution.',
      };
    }
    let answer = '';
    for (let e = 0; e < s.E; e++) answer += s.val[e] === LINE ? 'y' : 'n';
    return { ok: true, answer, edges: s.val, clues, width, height, stats: s.stats, ms };
  }

  // Text picture of a solved board, for the command line.
  function toText(result) {
    const W = result.width, H = result.height, HE = (H + 1) * W, edges = result.edges, clues = result.clues;
    const out = [];
    for (let r = 0; r <= H; r++) {
      let s = '';
      for (let c = 0; c < W; c++) s += '+' + (edges[r * W + c] === LINE ? '---' : '   ');
      out.push(s + '+');
      if (r === H) break;
      s = '';
      for (let c = 0; c <= W; c++) {
        s += edges[HE + r * (W + 1) + c] === LINE ? '|' : ' ';
        if (c < W) s += ' ' + (clues[r * W + c] >= 0 ? clues[r * W + c] : ' ') + ' ';
      }
      out.push(s);
    }
    return out.join('\n');
  }

  const api = { decodeTask, solveTask, toText };

  if (typeof module === 'object' && module.exports) {
    module.exports = api;
    return;
  }

  // ---------------------------------------------------------------------------
  // In the page
  // ---------------------------------------------------------------------------
  const win = window;

  // Two modes, remembered per browser:
  //   show: lay the answer over the board in blue; the game itself is not touched.
  //   draw: fill the answer in through the game, as if drawn by hand.
  const MODE_KEY = 'pls-mode';
  function getMode() { try { return localStorage.getItem(MODE_KEY) === 'draw' ? 'draw' : 'show'; } catch (e) { return 'show'; } }
  function setMode(m) { try { localStorage.setItem(MODE_KEY, m); } catch (e) { /* private window: mode just isn't remembered */ } }

  let cached = null;   // last solved puzzle, so Show/Hide doesn't solve again
  let overlay = null;  // the blue answer layer while it is showing

  function readPuzzle() {
    const game = win.Game;
    if (!game || !game.puzzleWidth || !game.currentState || !game.dom) throw new Error('No loop puzzle found on this page.');
    const scripts = Array.from(document.scripts).map(s => s.textContent).join('\n');
    let task = typeof win.task === 'string' ? win.task : null;
    if (!task) { const m = scripts.match(/var task = '([^']*)'/); task = m && m[1]; }
    if (!task) throw new Error('Could not find the puzzle data on this page.');
    const h = scripts.match(/hashedSolution:\s*'([0-9a-f]{32})'/);
    return { game, task, width: game.puzzleWidth, height: game.puzzleHeight, hash: h ? h[1] : null };
  }

  function solvePage(p) {
    if (cached && cached.task === p.task && cached.width === p.width && cached.height === p.height) return cached;
    const r = solveTask(p.task, p.width, p.height);
    if (!r.ok) throw new Error(r.message);
    let checked = false;
    if (p.hash && typeof win.md5 === 'function') {
      if (win.md5(p.task + r.answer) !== p.hash) throw new Error('Found a loop, but it does not match the page\'s answer check.');
      checked = true;
    }
    cached = { task: p.task, width: p.width, height: p.height, answer: r.answer, ms: r.ms, checked };
    return cached;
  }

  function overlayShowing() { return !!(overlay && overlay.isConnected); }
  function hideOverlay() { if (overlay) overlay.remove(); overlay = null; }

  // The answer as a see-through layer inside the board, so it follows the site's zoom and pan.
  // Each piece copies the position of the game's own edge, stretched over the dots at both ends.
  function showOverlay(game, width, height, answer) {
    hideOverlay();
    const parent = game.dom.cellsHorizontal[0][0][0].parentElement;
    const layer = document.createElement('div');
    layer.className = 'pls-overlay';
    layer.style.cssText = 'position:absolute;left:0;top:0;right:0;bottom:0;pointer-events:none;z-index:1000;opacity:.6';
    const add = (el, horizontal) => {
      const t = horizontal ? el.offsetHeight : el.offsetWidth;
      const d = document.createElement('div');
      d.style.cssText = 'position:absolute;background:#2563eb;border-radius:' + t + 'px;' + (horizontal
        ? 'left:' + (el.offsetLeft - t) + 'px;top:' + el.offsetTop + 'px;width:' + (el.offsetWidth + 2 * t) + 'px;height:' + t + 'px'
        : 'left:' + el.offsetLeft + 'px;top:' + (el.offsetTop - t) + 'px;width:' + t + 'px;height:' + (el.offsetHeight + 2 * t) + 'px');
      layer.appendChild(d);
    };
    let i = 0;
    for (let r = 0; r <= height; r++) for (let c = 0; c < width; c++, i++) if (answer[i] === 'y') add(game.dom.cellsHorizontal[r][c][0], true);
    for (let r = 0; r < height; r++) for (let c = 0; c <= width; c++, i++) if (answer[i] === 'y') add(game.dom.cellsVertical[r][c][0], false);
    parent.appendChild(layer);
    overlay = layer;
  }

  // Draw through the game's own move handling, so undo, saving and the solved check all behave
  // as if the lines were drawn by hand. One move can carry any number of edges.
  function applyMove(game, cells, status) {
    if (!cells.length) return;
    game.initCurrentMove();
    game.drawing = true;
    const m = game.currentMove;
    m.cellStatus = status;
    m.cells = cells;
    m.startPoint = cells[0];
    m.endPoint = cells[cells.length - 1];
    game.endMove();
  }

  function drawAnswer(game, width, height, answer) {
    const st = game.currentState, toClear = [], toDraw = [];
    let i = 0;
    for (let r = 0; r <= height; r++) {
      for (let c = 0; c < width; c++, i++) {
        const cur = st.cellHorizontalStatus[r][c], want = answer[i] === 'y';
        if (want && cur !== 1) toDraw.push({ row: r, col: c, type: 'horizontal' });
        else if (!want && cur === 1) toClear.push({ row: r, col: c, type: 'horizontal' });
      }
    }
    for (let r = 0; r < height; r++) {
      for (let c = 0; c <= width; c++, i++) {
        const cur = st.cellVerticalStatus[r][c], want = answer[i] === 'y';
        if (want && cur !== 1) toDraw.push({ row: r, col: c, type: 'vertical' });
        else if (!want && cur === 1) toClear.push({ row: r, col: c, type: 'vertical' });
      }
    }
    applyMove(game, toClear, 0);
    applyMove(game, toDraw, 1);
    return { drawn: toDraw.length, cleared: toClear.length };
  }

  function act(ui) {
    let p;
    try { p = readPuzzle(); } catch (e) { ui.status(e.message); return; }
    const mode = getMode();
    if (mode === 'show' && overlayShowing()) { hideOverlay(); ui.refresh(); ui.status('Answer hidden.'); return; }
    ui.status('Solving ' + p.width + ' x ' + p.height + '...');
    // Let the status paint before a long solve.
    setTimeout(() => {
      try {
        const s = solvePage(p);
        const how = 'solved in ' + s.ms + ' ms' + (s.checked ? ', matches the page\'s answer check' : '');
        if (mode === 'show') {
          showOverlay(p.game, p.width, p.height, s.answer);
          ui.status('Answer shown in blue (' + how + '). Your board is unchanged.');
        } else {
          hideOverlay();
          if (p.game.solved || p.game.currentState.solved) { ui.status('Already solved.'); }
          else {
            const d = drawAnswer(p.game, p.width, p.height, s.answer);
            ui.status('Drew ' + d.drawn + ' lines' + (d.cleared ? ' and removed ' + d.cleared + ' wrong ones' : '') + ' (' + how + ').');
          }
        }
      } catch (e) { ui.status(e.message); }
      ui.refresh();
    }, 30);
  }

  function buildPanel() {
    const existing = document.getElementById('pls-panel');
    if (existing) return existing.__ui;
    const font = '"Segoe UI",system-ui,sans-serif';
    const el = (tag, css, text) => {
      const e = document.createElement(tag);
      e.style.cssText = css;
      if (text) e.textContent = text;
      return e;
    };
    // Same look as the desktop tool and Puffer Helper: dark, white title, muted grey for everything else.
    const box = el('div', 'position:fixed;right:16px;bottom:16px;z-index:2147483647;width:260px;box-sizing:border-box;' +
      'font:13px/1.4 ' + font + ';color:#fff;text-align:left;background:#1c1c22;border:1px solid #2f2f38;border-radius:10px;' +
      'padding:10px 12px 12px;box-shadow:0 6px 20px rgba(0,0,0,.35)');
    box.id = 'pls-panel';

    // Title and close
    const head = el('div', 'display:flex;align-items:center;justify-content:space-between;margin-bottom:8px');
    const close = el('button', 'font:20px/1 ' + font + ';border:0;background:none;color:#a0a0ac;cursor:pointer;padding:0 2px', '×');
    close.title = 'Close';
    head.append(el('div', 'font:600 16px ' + font + ';color:#fff', 'Loop Solver'), close);

    // Show | Draw
    const seg = el('div', 'display:flex;border:1px solid #3a3a45;border-radius:6px;overflow:hidden;margin-bottom:8px');
    const segBtn = (label, tip) => {
      const b = el('button', 'flex:1;font:600 12px ' + font + ';padding:5px 0;border:0;cursor:pointer', label);
      b.title = tip;
      seg.appendChild(b);
      return b;
    };
    const bShow = segBtn('Show', 'Show the answer over the board without changing it');
    const bDraw = segBtn('Draw', 'Fill the answer in on the board');

    // The action, and what happened
    const go = el('button', 'display:block;width:100%;box-sizing:border-box;font:600 13px ' + font + ';padding:7px 0;' +
      'border:1px solid #3f3f4a;border-radius:6px;background:#2c2c35;color:#fff;cursor:pointer');
    go.addEventListener('mouseenter', () => { go.style.background = '#36363f'; });
    go.addEventListener('mouseleave', () => { go.style.background = '#2c2c35'; });
    const status = el('div', 'margin-top:8px;color:#a0a0ac;font-size:12px', 'Pick Show or Draw, then press the button.');

    box.append(head, seg, go, status);
    document.body.appendChild(box);

    const ui = {
      status(t) { status.textContent = t; },
      refresh() {
        const m = getMode();
        for (const [b, on] of [[bShow, m === 'show'], [bDraw, m === 'draw']]) {
          b.style.background = on ? '#3a3a45' : 'transparent';
          b.style.color = on ? '#fff' : '#a0a0ac';
        }
        go.textContent = m === 'draw' ? 'Draw answer' : (overlayShowing() ? 'Hide answer' : 'Show answer');
      },
    };
    bShow.addEventListener('click', () => { setMode('show'); ui.refresh(); });
    bDraw.addEventListener('click', () => { setMode('draw'); hideOverlay(); ui.refresh(); });
    go.addEventListener('click', () => act(ui));
    close.addEventListener('click', () => { hideOverlay(); box.remove(); });
    box.__ui = ui;
    ui.refresh();
    return ui;
  }

  win.__puzzleLoopSolver = api;
  if (!win.Game || !win.Game.dom) return;
  const ui = buildPanel();
  if (win.__plsRunNow) { win.__plsRunNow = 0; act(ui); }
})();

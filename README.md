# Puzzle Loop Solver

Solves Slitherlink puzzles on [puzzle-loop.com](https://www.puzzle-loop.com/) and draws the loop on the board.
It works for every size the site offers, from 5x5 up to the 40x50 Special Monthly.

No screenshots, no text recognition, no AI. The puzzle page already contains the numbers as text, so the
solver reads them directly, works out the loop, checks the answer against the page's own answer check, and
then draws the lines.

## Using it

Pick one of the two ways below.

### Option A: userscript (adds a Solve button to every puzzle)

1. Install the **Tampermonkey** browser extension.
2. Open Tampermonkey's dashboard, choose **Create a new script**, delete the template, paste the whole of
   `puzzle-loop-solver.user.js`, and save.
3. Open any puzzle on puzzle-loop.com. A **Solve** button appears in the bottom-right corner. Click it.

### Option B: bookmarklet (no extension)

1. Build it once:

   ```powershell
   node make-bookmarklet.js
   ```

2. Create a new bookmark in your browser. Paste the entire contents of `bookmarklet.txt` as its URL.
3. Open a puzzle and click the bookmark. It solves and draws straight away.

## What happens when you click Solve

1. Reads the puzzle from the page.
2. Solves it (milliseconds for most sizes, about a third of a second for the 40x50 Monthly).
3. Checks the answer against the solution check the page ships with. If it doesn't match, nothing is drawn.
4. Draws the loop in one step. Any wrong lines you drew yourself are removed. Undo still works.

If the site's **auto-submit** setting is on, the site marks the puzzle solved as soon as the last line
lands. Nothing is ever sent to the Hall of Fame; that stays a separate button.

## How the solver works

The board is a set of edges, each one "line", "no line" or "unknown". It repeats these rules until nothing
changes:

- **Numbers:** once a number has all its lines, the rest of its edges are "no line". If it needs every
  remaining edge, they are all lines.
- **Dots:** every dot has either 0 or 2 lines. One line in with one way out means that way is a line.
- **One loop only:** a line may not close a loop unless that loop uses every line on the board and meets
  every number.
- **Trial:** for each unknown edge, try "line" and "no line". If one leads to a contradiction, the other
  is right. If both lead to the same result somewhere else, that result is right.
- **Guess:** if the rules above get stuck (rare, only seen on the Monthly), it guesses one edge and backs
  out if that guess fails.

## Command line and tests

Solve a puzzle from its task text and size, optionally checking it against the page's hash:

```powershell
node cli.js c2a31323a22031g1a 5 5 90658ad1420ccd72d733a4417daf3944
```

Run the solver over the 15 real puzzles in `test/puzzles.json` (every size on the site), checking each answer
against the site's own hash:

```powershell
node test/run-tests.js
```

## Files

| File | What's in it |
|---|---|
| `puzzle-loop-solver.user.js` | The solver and the page button. Also loads in Node for the tools below. |
| `cli.js` | Solve one puzzle from the command line and print the loop. |
| `make-bookmarklet.js` | Builds `bookmarklet.txt` from the userscript. |
| `test/puzzles.json`, `test/run-tests.js` | Real puzzles from the site with their answer hashes, and the test runner. |

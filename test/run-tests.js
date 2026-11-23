// Solves every puzzle in puzzles.json and checks each answer against the site's own hash:
// md5(task + answer) must equal the hashedSolution the page shipped with.
// Run: node test/run-tests.js
'use strict';
const crypto = require('crypto');
const path = require('path');
const { solveTask } = require('../browser/puzzle-loop-solver.user.js');
const puzzles = require('./puzzles.json');

let failed = 0;
for (const p of puzzles) {
  const r = solveTask(p.task, p.w, p.h);
  let verdict;
  if (!r.ok) verdict = 'FAIL  ' + r.message;
  else if (crypto.createHash('md5').update(p.task + r.answer).digest('hex') !== p.hash) verdict = 'FAIL  answer does not match the hash';
  else verdict = 'ok';
  if (verdict !== 'ok') failed++;
  const s = r.stats;
  console.log(
    p.name.padEnd(24) + verdict.padEnd(8) +
    String(r.ms).padStart(6) + ' ms   ' +
    'passes ' + String(s.passes).padStart(3) + '   guesses ' + String(s.branches).padStart(4) + '   probes ' + s.probes
  );
}
console.log(failed ? failed + ' of ' + puzzles.length + ' failed' : 'All ' + puzzles.length + ' solved and match the site\'s hash.');
process.exit(failed ? 1 : 0);

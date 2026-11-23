// Solve a puzzle from the command line and print the loop.
//   node cli.js <task> <width> <height> [hashedSolution]
// Example:
//   node cli.js c2a31323a22031g1a 5 5 90658ad1420ccd72d733a4417daf3944
'use strict';
const crypto = require('crypto');
const { solveTask, toText } = require('./puzzle-loop-solver.user.js');

const [task, w, h, hash] = process.argv.slice(2);
if (!task || !w || !h) {
  console.log('Usage: node cli.js <task> <width> <height> [hashedSolution]');
  process.exit(2);
}
const r = solveTask(task, Number(w), Number(h));
if (!r.ok) { console.log(r.message); process.exit(1); }
console.log(toText(r));
console.log('\nSolved in ' + r.ms + ' ms (' + r.stats.branches + ' guesses).');
if (hash) {
  const ok = crypto.createHash('md5').update(task + r.answer).digest('hex') === hash;
  console.log(ok ? 'Matches the page\'s answer check.' : 'Does NOT match the page\'s answer check.');
  process.exit(ok ? 0 : 1);
}

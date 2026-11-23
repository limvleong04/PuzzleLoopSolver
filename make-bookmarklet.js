// Builds bookmarklet.txt from the userscript. Paste the file's contents as the URL of a new bookmark.
// Clicking the bookmark on a puzzle-loop.com puzzle page solves and draws it straight away.
//   node make-bookmarklet.js
'use strict';
const fs = require('fs');
const path = require('path');

const src = fs.readFileSync(path.join(__dirname, 'puzzle-loop-solver.user.js'), 'utf8');
const code = 'window.__plsRunNow=1;' + src;
const url = 'javascript:' + encodeURIComponent(code);
fs.writeFileSync(path.join(__dirname, 'bookmarklet.txt'), url);
console.log('Wrote bookmarklet.txt (' + url.length + ' characters).');

'use strict';
const fs = require('fs');
const pngToIco = require('png-to-ico');

(async () => {
  const ico = await pngToIco('icon-256.png');
  fs.writeFileSync('icon.ico', ico);
})().catch(error => {
  console.error(error);
  process.exit(1);
});

// Developer workbench tests. Synthetic photographs only; no camera or user files.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const zlib = require('node:zlib');
const { pathToFileURL } = require('node:url');
const { chromium } = require('playwright');

const output = path.resolve(__dirname, '../../artifacts/piece-annotation-smoke');
fs.mkdirSync(output, { recursive: true });
function crc32(bytes) {
  let crc = 0xffffffff;
  for (const byte of bytes) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ (crc & 1 ? 0xedb88320 : 0);
  }
  return (crc ^ 0xffffffff) >>> 0;
}
function png(width, height) {
  const chunk = (type, data) => {
    const typed = Buffer.concat([Buffer.from(type), data]);
    const result = Buffer.alloc(typed.length + 8);
    result.writeUInt32BE(data.length); typed.copy(result, 4);
    result.writeUInt32BE(crc32(typed), result.length - 4); return result;
  };
  const header = Buffer.alloc(13); header.writeUInt32BE(width); header.writeUInt32BE(height, 4);
  header[8] = 8; header[9] = 2;
  const pixels = Buffer.alloc((width * 3 + 1) * height);
  for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
    const i = y * (width * 3 + 1) + 1 + x * 3;
    const train = x > 150 && x < 280 && y > 120 && y < 170;
    pixels[i] = train ? 20 : 236; pixels[i + 1] = train ? 90 : 219; pixels[i + 2] = train ? 140 : 182;
  }
  return Buffer.concat([Buffer.from('89504e470d0a1a0a', 'hex'), chunk('IHDR', header),
    chunk('IDAT', zlib.deflateSync(pixels)), chunk('IEND', Buffer.alloc(0))]);
}
const photos = ['synthetic-board.png', 'synthetic-empty.png'].map(name => path.join(output, name));
photos.forEach(file => fs.writeFileSync(file, png(800, 500)));

(async () => {
  const executable = process.env.GOLDENTICKET_TEST_BROWSER ||
    'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';
  const browser = await chromium.launch({ headless: true, executablePath: executable });
  const checks = [], errors = [], external = [];
  try {
    const context = await browser.newContext({ viewport: { width: 1440, height: 1100 }, offline: true });
    await context.route(/^https?:/, route => { external.push(route.request().url()); return route.abort(); });
    const page = await context.newPage();
    page.on('pageerror', error => errors.push(error.message));
    page.on('dialog', dialog => dialog.accept());
    await page.goto(pathToFileURL(path.join(__dirname, 'annotate.html')).href);
    await page.locator('#image-files').setInputFiles(photos);
    await page.locator('#image-list button').nth(1).waitFor();
    assert.equal(await page.locator('#image-list button').count(), 2);
    checks.push('Local PNG import while browser is offline');

    await page.locator('#capture-group').fill('session-01');
    await page.locator('#piece-kind').selectOption('train');
    await page.locator('#piece-color').selectOption('blue');
    const canvas = page.locator('#annotation-canvas');
    await canvas.scrollIntoViewIfNeeded();
    const rect = await canvas.boundingBox();
    await page.mouse.move(rect.x + rect.width * .2, rect.y + rect.height * .25);
    await page.mouse.down();
    await page.mouse.move(rect.x + rect.width * .35, rect.y + rect.height * .35, { steps: 5 });
    await page.mouse.up();
    assert.equal(await page.locator('#box-list button').count(), 1);
    await page.locator('#image-reviewed').check();
    checks.push('Scaled mouse drawing and explicit review');

    async function exportProject(name) {
      const downloadEvent = page.waitForEvent('download');
      await page.locator('#export-labels').click();
      const download = await downloadEvent;
      const destination = path.join(output, name); await download.saveAs(destination);
      return { destination, project: JSON.parse(fs.readFileSync(destination, 'utf8')) };
    }
    const initial = await exportProject('labels.json');
    const first = initial.project.images.find(image => image.fileName === 'synthetic-board.png');
    assert.equal(first.width, 800); assert.equal(first.height, 500); assert.equal(first.reviewed, true);
    assert.equal(first.boxes[0].kind, 'train'); assert.equal(first.boxes[0].color, 'blue');
    for (const [key, expected] of Object.entries({ x: 160, y: 125, width: 120, height: 50 })) {
      assert.ok(Math.abs(first.boxes[0][key] - expected) < 3, `${key} must use source pixels`);
    }
    checks.push('Export preserves source coordinates and reviewed labels');

    await page.locator('#piece-color').selectOption('green');
    assert.equal(await page.locator('#image-reviewed').isChecked(), false);
    await page.locator('#label-file').setInputFiles(initial.destination);
    await page.waitForFunction(() => document.querySelector('#image-reviewed').checked);
    await page.locator('#box-list button').first().click();
    assert.equal(await page.locator('#piece-color').inputValue(), 'blue');
    checks.push('Editing invalidates review and JSON import restores labels');

    // An invalid later record must not partially replace the earlier valid record.
    const invalid = structuredClone(initial.project);
    invalid.images[0].group = 'should-not-be-applied';
    invalid.images[1].boxes = [{ kind: 'train', color: 'red', x: -2, y: 0, width: 30, height: 20 }];
    const invalidPath = path.join(output, 'invalid.json'); fs.writeFileSync(invalidPath, JSON.stringify(invalid));
    await page.locator('#label-file').setInputFiles(invalidPath);
    await page.locator('#error').waitFor({ state: 'visible' });
    assert.equal(await page.locator('#capture-group').inputValue(), 'session-01');
    checks.push('Invalid imports rejected atomically');

    const wrongHash = structuredClone(initial.project);
    wrongHash.images[0].sha256 = '0'.repeat(64);
    const wrongHashPath = path.join(output, 'wrong-hash.json');
    fs.writeFileSync(wrongHashPath, JSON.stringify(wrongHash));
    await page.locator('#label-file').setInputFiles(wrongHashPath);
    await page.waitForFunction(() => document.querySelector('#error').textContent.includes('SHA-256'));
    assert.equal(await page.locator('#box-list button').count(), 1);
    checks.push('Changed source identity cannot silently reuse labels');

    // Reject orientation 3 (180 degrees) before browser decoding: dimensions alone cannot catch it.
    const tiff = Buffer.alloc(26);
    tiff.write('II'); tiff.writeUInt16LE(42, 2); tiff.writeUInt32LE(8, 4);
    tiff.writeUInt16LE(1, 8); tiff.writeUInt16LE(0x112, 10); tiff.writeUInt16LE(3, 12);
    tiff.writeUInt32LE(1, 14); tiff.writeUInt16LE(3, 18);
    const exif = Buffer.concat([Buffer.from('Exif\0\0'), tiff]);
    const app1 = Buffer.alloc(4); app1[0] = 0xff; app1[1] = 0xe1; app1.writeUInt16BE(exif.length + 2, 2);
    await page.locator('#image-files').setInputFiles({ name: 'rotated.jpg', mimeType: 'image/jpeg',
      buffer: Buffer.concat([Buffer.from([0xff, 0xd8]), app1, exif, Buffer.from([0xff, 0xd9])]) });
    await page.waitForFunction(() => document.querySelector('#error').textContent.includes('upright'));
    assert.equal(await page.locator('#image-list button').count(), 2);
    checks.push('JPEG orientation cannot silently rotate annotation coordinates');

    await page.locator('#capture-group').focus();
    await page.keyboard.press('Delete');
    assert.equal(await page.locator('#box-list button').count(), 1);
    await canvas.focus(); await page.keyboard.press('Delete');
    assert.equal(await page.locator('#box-list button').count(), 0);
    assert.equal(await page.locator('#image-reviewed').isChecked(), false);
    checks.push('Delete only removes a box with canvas focus');

    await page.locator('#piece-kind').selectOption('player-marker');
    await page.locator('#piece-color').selectOption('red');
    await canvas.scrollIntoViewIfNeeded();
    const markerRect = await canvas.boundingBox();
    await page.mouse.move(markerRect.x + markerRect.width * .75, markerRect.y + markerRect.height * .1);
    await page.mouse.down();
    await page.mouse.move(markerRect.x + markerRect.width * .8, markerRect.y + markerRect.height * .18);
    await page.mouse.up();
    const markerProject = await exportProject('marker-labels.json');
    assert.equal(markerProject.project.images[0].boxes[0].kind, 'player-marker');
    assert.equal(markerProject.project.images[0].boxes[0].color, 'red');
    await canvas.focus(); await page.keyboard.press('Delete');
    checks.push('Player marker class and physical color export independently');

    // Both selected photos intentionally share one group. Split mismatch is blocked by exporter.
    await page.locator('#image-reviewed').check();
    await page.locator('#image-list button').nth(1).click();
    await page.locator('#capture-group').fill('session-01');
    await page.locator('#image-reviewed').check();
    const negatives = await exportProject('negative-labels.json');
    assert.ok(negatives.project.images.every(image => image.reviewed && image.boxes.length === 0));
    checks.push('Reviewed zero-box negative photos survive export');

    await page.locator('#label-file').setInputFiles(initial.destination);
    await page.locator('#image-list button').first().click();
    await page.screenshot({ path: path.join(output, 'desktop.png'), fullPage: true });
    await page.setViewportSize({ width: 390, height: 844 });
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
    await page.screenshot({ path: path.join(output, 'narrow.png'), fullPage: true });
    checks.push('Desktop and narrow layouts have no horizontal overflow');

    assert.deepEqual(errors, []); assert.deepEqual(external, []);
    checks.push('No JavaScript errors or HTTP/HTTPS requests');
    fs.writeFileSync(path.join(output, 'results.json'), JSON.stringify({ checks, errors, external }, null, 2));
    console.log(`${checks.length} annotation workbench checks passed.`);
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });

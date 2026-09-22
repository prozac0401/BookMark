#!/usr/bin/env node
// Requires Node.js 20+ and sharp. Set NODE_PATH if sharp is installed outside this repository.
// All shapes are authored in the checked-in SVG files; no generated or remote art is used.
// Example: node scripts/build-brand-assets.cjs
const fs = require('node:fs/promises');
const path = require('node:path');
const sharp = require('sharp');

const root = path.resolve(__dirname, '..');
const appDir = path.join(root, 'src', 'WorkBookmark.App', 'Assets');
const installerDir = path.join(root, 'installer', 'Assets');
const evidenceDir = path.join(root, 'docs', 'evidence', 'branding-2026-09-22');
const sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

async function raster(file, width, height = width) {
  return sharp(await fs.readFile(file), { density: 384 })
    .resize(width, height, { fit: 'fill', kernel: 'lanczos3' }).png().toBuffer();
}

// Windows supports PNG-compressed frames in ICO resources. Include native DPI sizes
// instead of requiring the shell to stretch a single 16px or 32px bitmap.
async function writeIco(output, resolveSource) {
  const frames = [];
  for (const size of sizes) frames.push(await raster(resolveSource(size), size));
  const header = Buffer.alloc(6 + 16 * frames.length);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(frames.length, 4);
  let offset = header.length;
  frames.forEach((frame, i) => {
    const entry = 6 + i * 16;
    header[entry] = sizes[i] === 256 ? 0 : sizes[i];
    header[entry + 1] = header[entry];
    header.writeUInt16LE(1, entry + 4);
    header.writeUInt16LE(32, entry + 6);
    header.writeUInt32LE(frame.length, entry + 8);
    header.writeUInt32LE(offset, entry + 12);
    offset += frame.length;
  });
  await fs.writeFile(output, Buffer.concat([header, ...frames]));
}

// WiX bitmap controls expect uncompressed 24-bit BMP. Rows are bottom-up, BGR,
// padded to a four-byte boundary. Avoid indexed palettes or alpha in installer art.
async function writeBmp(svg, output, width, height) {
  const pixels = await sharp(await raster(svg, width, height))
    .flatten({ background: '#FFFFFF' }).removeAlpha().raw().toBuffer();
  const stride = Math.ceil(width * 3 / 4) * 4;
  const data = Buffer.alloc(54 + stride * height);
  data.write('BM');
  data.writeUInt32LE(data.length, 2);
  data.writeUInt32LE(54, 10);
  data.writeUInt32LE(40, 14);
  data.writeInt32LE(width, 18);
  data.writeInt32LE(height, 22);
  data.writeUInt16LE(1, 26);
  data.writeUInt16LE(24, 28);
  data.writeUInt32LE(stride * height, 34);
  data.writeInt32LE(3780, 38);
  data.writeInt32LE(3780, 42);
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const source = (y * width + x) * 3;
      const destination = 54 + (height - 1 - y) * stride + x * 3;
      data[destination] = pixels[source + 2];
      data[destination + 1] = pixels[source + 1];
      data[destination + 2] = pixels[source];
    }
  }
  await fs.writeFile(output, data);
}

async function makePreview() {
  const appSvg = path.join(appDir, 'WorkBookmark.svg');
  const traySvg = path.join(appDir, 'WorkBookmark.Tray.svg');
  const background = Buffer.from(`<svg xmlns="http://www.w3.org/2000/svg" width="1120" height="800">
    <rect width="1120" height="800" fill="#F1F5F5"/>
    <text x="34" y="45" fill="#17343E" font-family="Segoe UI, sans-serif" font-size="26" font-weight="600">WorkBookmark</text>
    <text x="35" y="70" fill="#657A82" font-family="Segoe UI, sans-serif" font-size="13">Identity &amp; Windows surfaces · 22 September 2026</text>
    <rect x="30" y="94" width="242" height="252" rx="18" fill="#FFFFFF"/>
    <text x="50" y="326" fill="#657A82" font-family="Segoe UI, sans-serif" font-size="12">Application · 16–256 px</text>
    <rect x="292" y="94" width="798" height="119" rx="16" fill="#FFFFFF"/>
    <rect x="292" y="227" width="798" height="119" rx="16" fill="#20252C"/>
    <text x="313" y="123" fill="#657A82" font-family="Segoe UI, sans-serif" font-size="12">TRAY · LIGHT TASKBAR · NATIVE PIXEL SIZES</text>
    <text x="313" y="256" fill="#B8C6CC" font-family="Segoe UI, sans-serif" font-size="12">TRAY · DARK TASKBAR · NATIVE PIXEL SIZES</text>
    <text x="35" y="388" fill="#17343E" font-family="Segoe UI, sans-serif" font-size="15" font-weight="600">Installer welcome · 493 × 312</text>
    <text x="585" y="388" fill="#17343E" font-family="Segoe UI, sans-serif" font-size="15" font-weight="600">Installer header · 493 × 58</text>
    <rect x="583" y="500" width="496" height="227" rx="16" fill="#FFFFFF"/>
    <text x="608" y="534" fill="#17343E" font-family="Segoe UI, sans-serif" font-size="15" font-weight="600">Ready for Windows</text>
    <text x="608" y="564" fill="#657A82" font-family="Segoe UI, sans-serif" font-size="13">A folded ribbon marks a place worth returning to.</text>
    <text x="608" y="594" fill="#657A82" font-family="Segoe UI, sans-serif" font-size="13">Teal fill + ink contour retain contrast on both themes.</text>
    <text x="608" y="624" fill="#657A82" font-family="Segoe UI, sans-serif" font-size="13">Dedicated small artwork keeps the 16 px icon clear.</text>
    <text x="608" y="654" fill="#657A82" font-family="Segoe UI, sans-serif" font-size="13">White installer areas remain clear for localized text.</text>
    <rect x="608" y="679" width="28" height="24" rx="5" fill="#16333F"/>
    <rect x="646" y="679" width="28" height="24" rx="5" fill="#61DFC5"/>
    <rect x="684" y="679" width="28" height="24" rx="5" fill="#31BFAE"/>
    <rect x="722" y="679" width="28" height="24" rx="5" fill="#F1F5F5"/>
  </svg>`);
  const layers = [{ input: await raster(appSvg, 180), left: 61, top: 108 }];
  for (const [i, size] of [16, 20, 24, 32].entries()) {
    const source = size <= 24 ? path.join(appDir, 'WorkBookmark.Small.svg') : appSvg;
    layers.push({ input: await raster(source, size), left: 70 + i * 53 - Math.round(size / 2), top: 299 - Math.round(size / 2) });
  }
  const previewSizes = [16, 20, 24, 32, 40, 48];
  for (const [i, size] of previewSizes.entries()) {
    const x = 343 + i * 126 - Math.round(size / 2);
    layers.push({ input: await raster(traySvg, size), left: x, top: 163 - Math.round(size / 2) });
    layers.push({ input: await raster(traySvg, size), left: x, top: 296 - Math.round(size / 2) });
    const label = Buffer.from(`<svg xmlns="http://www.w3.org/2000/svg" width="52" height="18"><text x="26" y="13" text-anchor="middle" fill="#71858F" font-family="Segoe UI, sans-serif" font-size="10">${size} px</text></svg>`);
    layers.push({ input: label, left: 317 + i * 126, top: 188 });
    layers.push({ input: label, left: 317 + i * 126, top: 321 });
  }
  layers.push({ input: await raster(path.join(installerDir, 'dialog.svg'), 493, 312), left: 35, top: 410 });
  layers.push({ input: await raster(path.join(installerDir, 'banner.svg'), 493, 58), left: 585, top: 410 });
  await fs.mkdir(evidenceDir, { recursive: true });
  await sharp(background).composite(layers).png().toFile(path.join(evidenceDir, 'branding-preview.png'));
}

async function main() {
  await writeIco(path.join(appDir, 'WorkBookmark.ico'), size => path.join(appDir, size <= 24 ? 'WorkBookmark.Small.svg' : 'WorkBookmark.svg'));
  await writeIco(path.join(appDir, 'WorkBookmark.Tray.ico'), () => path.join(appDir, 'WorkBookmark.Tray.svg'));
  await writeBmp(path.join(installerDir, 'dialog.svg'), path.join(installerDir, 'dialog.bmp'), 493, 312);
  await writeBmp(path.join(installerDir, 'banner.svg'), path.join(installerDir, 'banner.bmp'), 493, 58);
  await makePreview();
  console.log('Generated app + tray ICO files, WiX BMP artwork, and branding preview.');
}

main().catch(error => { console.error(error); process.exitCode = 1; });

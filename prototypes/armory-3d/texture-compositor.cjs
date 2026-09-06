// The exporter uses integer DB2 texture rectangles and Canvas's default sRGB
// source-over operation. Keep the working surface premultiplied so transparent
// texels cannot introduce colored fringes when a texture is resized.
const MAX_PIXELS = 16 * 1024 * 1024;

function dimensions(width, height) {
  if (!Number.isSafeInteger(width) || !Number.isSafeInteger(height)
      || width < 1 || height < 1 || width > 8192 || height > 8192 || width * height > MAX_PIXELS)
    throw new Error('Invalid texture dimensions');
}

function premultiply(image) {
  dimensions(image?.width, image?.height);
  if (!(image.data instanceof Uint8Array) || image.data.length !== image.width * image.height * 4)
    throw new Error('Invalid RGBA texture data');
  const pixels = Buffer.alloc(image.data.length);
  for (let i = 0; i < pixels.length; i += 4) {
    const alpha = image.data[i + 3];
    pixels[i] = Math.round(image.data[i] * alpha / 255);
    pixels[i + 1] = Math.round(image.data[i + 1] * alpha / 255);
    pixels[i + 2] = Math.round(image.data[i + 2] * alpha / 255);
    pixels[i + 3] = alpha;
  }
  return pixels;
}

function unpremultiply(surface) {
  const pixels = Buffer.alloc(surface.data.length);
  for (let i = 0; i < pixels.length; i += 4) {
    const alpha = surface.data[i + 3];
    if (!alpha) continue;
    pixels[i] = Math.min(255, Math.round(surface.data[i] * 255 / alpha));
    pixels[i + 1] = Math.min(255, Math.round(surface.data[i + 1] * 255 / alpha));
    pixels[i + 2] = Math.min(255, Math.round(surface.data[i + 2] * 255 / alpha));
    pixels[i + 3] = alpha;
  }
  return {width: surface.width, height: surface.height, data: pixels};
}

function drawLayer(surface, entry, source) {
  const {X: x, Y: y, Width: width, Height: height} = entry.section ?? {};
  if (![x, y, width, height].every(Number.isSafeInteger) || width < 1 || height < 1
      || Math.abs(x) > 8192 || Math.abs(y) > 8192 || width > 8192 || height > 8192)
    throw new Error('Invalid integer texture section');
  if (![0, 1, 9, 15].includes(entry.mode)) throw new Error('Unsupported compositing mode');
  const left = Math.max(0, x), top = Math.max(0, y);
  const right = Math.min(surface.width, x + width), bottom = Math.min(surface.height, y + height);
  if (left >= right || top >= bottom) return;
  if (entry.mode === 0 || entry.mode === 1) {
    for (let row = top; row < bottom; row++)
      surface.data.fill(0, (row * surface.width + left) * 4, (row * surface.width + right) * 4);
  }
  const sourceWidth = entry.image.width, sourceHeight = entry.image.height;
  for (let row = top; row < bottom; row++) {
    const sy = Math.max(0, Math.min(sourceHeight - 1, (row - y + .5) * sourceHeight / height - .5));
    const y0 = Math.floor(sy), y1 = Math.min(sourceHeight - 1, y0 + 1), fy = sy - y0;
    for (let column = left; column < right; column++) {
      const sx = Math.max(0, Math.min(sourceWidth - 1, (column - x + .5) * sourceWidth / width - .5));
      const x0 = Math.floor(sx), x1 = Math.min(sourceWidth - 1, x0 + 1), fx = sx - x0;
      const p00 = (y0 * sourceWidth + x0) * 4, p10 = (y0 * sourceWidth + x1) * 4;
      const p01 = (y1 * sourceWidth + x0) * 4, p11 = (y1 * sourceWidth + x1) * 4;
      const w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy), w01 = (1 - fx) * fy, w11 = fx * fy;
      const sample = channel => source[p00 + channel] * w00 + source[p10 + channel] * w10
        + source[p01 + channel] * w01 + source[p11 + channel] * w11;
      const alpha = sample(3), destination = (row * surface.width + column) * 4;
      if (!alpha) continue;
      const remainder = 1 - alpha / 255;
      for (let channel = 0; channel < 3; channel++)
        surface.data[destination + channel] = Math.round(sample(channel) + surface.data[destination + channel] * remainder);
      surface.data[destination + 3] = Math.round(alpha + surface.data[destination + 3] * remainder);
    }
  }
}

function composeTextureLayers(entries) {
  if (!Array.isArray(entries) || entries.length > 256) throw new Error('Invalid texture layer collection');
  const surfaces = new Map(), decoded = new WeakMap();
  for (const entry of [...entries].sort((a, b) => a.order - b.order)) {
    if (!entry || !Number.isFinite(entry.order) || !Number.isSafeInteger(entry.type)) throw new Error('Invalid texture layer');
    const width = entry.material?.Width, height = entry.material?.Height;
    dimensions(width, height);
    let surface = surfaces.get(entry.type);
    if (!surface) {
      surface = {width, height, data: Buffer.alloc(width * height * 4)};
      surfaces.set(entry.type, surface);
    } else if (surface.width !== width || surface.height !== height) throw new Error('Conflicting texture material dimensions');
    let source = entry.image && decoded.get(entry.image);
    if (!source) {
      source = premultiply(entry.image);
      decoded.set(entry.image, source);
    }
    drawLayer(surface, entry, source);
  }
  return new Map(Array.from(surfaces, ([type, surface]) => [type, unpremultiply(surface)]));
}

module.exports = {composeTextureLayers};

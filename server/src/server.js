import { createReadStream, existsSync, statSync } from "node:fs";
import { readFile } from "node:fs/promises";
import { createServer } from "node:http";
import { extname, join, normalize, resolve, sep } from "node:path";

const port = Number(process.env.PORT || 4322);
const feedRoot = resolve(process.env.WOTLK_FEED_ROOT || "/srv/wotlk/launcher-feed");
const token = process.env.WOTLK_LAUNCHER_TOKEN || "";
const publicBaseUrl = (process.env.WOTLK_PUBLIC_BASE_URL || "http://152.228.225.7/wotlk/").replace(/\/?$/, "/");

function send(res, status, body, headers = {}) {
  const text = typeof body === "string" ? body : JSON.stringify(body);
  res.writeHead(status, {
    "content-type": typeof body === "string" ? "text/plain; charset=utf-8" : "application/json; charset=utf-8",
    "cache-control": "no-store",
    ...headers,
  });
  res.end(text);
}

function isAuthorized(req) {
  if (!token) {
    return false;
  }

  const header = req.headers.authorization || "";
  return header === `Bearer ${token}`;
}

function safeJoin(root, relativePath) {
  const decoded = decodeURIComponent(relativePath);
  const normalized = normalize(decoded).replace(/^(\.\.(\/|\\|$))+/, "");
  const fullPath = resolve(join(root, normalized));
  const requiredPrefix = root.endsWith(sep) ? root : root + sep;

  if (fullPath !== root && !fullPath.startsWith(requiredPrefix)) {
    throw new Error("Invalid path");
  }

  return fullPath;
}

function contentType(path) {
  switch (extname(path).toLowerCase()) {
    case ".json":
      return "application/json; charset=utf-8";
    case ".wtf":
    case ".toc":
    case ".lua":
    case ".xml":
    case ".txt":
      return "text/plain; charset=utf-8";
    default:
      return "application/octet-stream";
  }
}

async function handleManifest(res) {
  const manifestPath = join(feedRoot, "manifest.json");
  const manifest = JSON.parse(await readFile(manifestPath, "utf8"));
  manifest.baseUrl = publicBaseUrl;

  send(res, 200, manifest, {
    "cache-control": "private, no-store",
  });
}

function handleFile(req, res, relativePath) {
  const filePath = safeJoin(join(feedRoot, "files"), relativePath);
  if (!existsSync(filePath)) {
    send(res, 404, "Not Found");
    return;
  }

  const stats = statSync(filePath);
  if (!stats.isFile()) {
    send(res, 404, "Not Found");
    return;
  }

  res.writeHead(200, {
    "content-type": contentType(filePath),
    "content-length": stats.size,
    "cache-control": "private, max-age=3600",
  });
  createReadStream(filePath).pipe(res);
}

const server = createServer(async (req, res) => {
  try {
    const url = new URL(req.url || "/", "http://localhost");

    if (req.method === "GET" && url.pathname === "/health") {
      send(res, 200, "ok");
      return;
    }

    if (!isAuthorized(req)) {
      send(res, 401, "Unauthorized", {
        "www-authenticate": "Bearer",
      });
      return;
    }

    if (req.method === "GET" && url.pathname === "/wotlk/manifest.json") {
      await handleManifest(res);
      return;
    }

    if (req.method === "GET" && url.pathname.startsWith("/wotlk/files/")) {
      handleFile(req, res, url.pathname.slice("/wotlk/files/".length));
      return;
    }

    send(res, 404, "Not Found");
  } catch (error) {
    console.error(error);
    send(res, 500, "Internal Server Error");
  }
});

server.listen(port, "127.0.0.1", () => {
  console.log(`wotlk-launcher-server listening on 127.0.0.1:${port}`);
  console.log(`feed root: ${feedRoot}`);
});

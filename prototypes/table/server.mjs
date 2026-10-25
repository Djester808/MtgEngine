import http from "node:http";
import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";
const root = path.dirname(fileURLToPath(import.meta.url));
const types = {
  ".html": "text/html",
  ".js": "text/javascript",
  ".css": "text/css",
};
const server = http.createServer(async (req, res) => {
  const name = new URL(req.url, "http://localhost").pathname;
  const file = name === "/" ? "index.html" : name.slice(1);
  if (
    ![
      "index.html",
      "app.js",
      "model.js",
      "styles.css",
      "components.css",
      "battlefield.css",
      "battlefield-layout.js",
      "arrangements.js",
    ].includes(file)
  ) {
    res.writeHead(404).end();
    return;
  }
  try {
    const body = await readFile(path.join(root, file));
    res.writeHead(200, {
      "Content-Type": `${types[path.extname(file)]}; charset=utf-8`,
      "Cache-Control": "no-store",
    });
    res.end(body);
  } catch {
    res.writeHead(500).end("Unable to load prototype.");
  }
});
server.listen(4317, "127.0.0.1", () =>
  console.log("Table prototype: http://127.0.0.1:4317"),
);

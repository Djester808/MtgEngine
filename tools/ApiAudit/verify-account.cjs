const path = require("path"),
  fs = require("fs"),
  https = require("https"),
  crypto = require("crypto"),
  assert = require("assert");
const e2e = path.resolve("../mtg-client/e2e");
const { By, until } = require(
  path.join(e2e, "node_modules/selenium-webdriver"),
);
const { buildDriver } = require(path.join(e2e, "helpers/driver"));
const ForumPage = require(path.join(e2e, "pages/ForumPage"));
const out = path.resolve("audit-live-account");
fs.mkdirSync(out, { recursive: true });
const username = "audit_" + Date.now(),
  password = "Audit!" + crypto.randomBytes(18).toString("hex");
let token, driver;
function api(method, url, body, authorized = true) {
  return new Promise((resolve, reject) => {
    const req = https.request(
      "https://localhost:7001" + url,
      {
        method,
        rejectUnauthorized: false,
        headers: {
          "Content-Type": "application/json",
          ...(body
            ? { "Content-Length": Buffer.byteLength(JSON.stringify(body)) }
            : {}),
          ...(authorized && token ? { Authorization: "Bearer " + token } : {}),
        },
      },
      (res) => {
        let raw = "";
        res.on("data", (c) => (raw += c));
        res.on("end", () =>
          resolve({
            status: res.statusCode,
            data: raw ? JSON.parse(raw) : null,
          }),
        );
      },
    );
    req.on("error", reject);
    if (body) req.write(JSON.stringify(body));
    req.end();
  });
}
async function visible(css) {
  const el = await driver.wait(until.elementLocated(By.css(css)), 15000);
  await driver.wait(until.elementIsVisible(el), 15000);
  return el;
}
async function shot(name) {
  fs.writeFileSync(
    path.join(out, name + ".png"),
    await driver.takeScreenshot(),
    "base64",
  );
}
async function navigate(url) {
  await driver.get(url);
  await driver.wait(
    () =>
      driver.executeScript(
        `document.querySelector('.brand-intro:not(.is-exiting) .intro-skip')?.click(); const shell=document.querySelector('.app-shell');return !!shell&&!shell.hasAttribute('inert');`,
      ),
    20000,
  );
}
async function login() {
  await navigate("http://localhost:4200/login");
  await (await visible("#username")).sendKeys(username.toUpperCase());
  await (await visible("#password")).sendKeys(password);
  await (await visible(".submit-btn")).click();
  await driver.wait(
    async () => !(await driver.getCurrentUrl()).includes("/login"),
    15000,
  );
  console.log("PASS uppercase username browser login");
}
(async () => {
  try {
    let r = await api(
      "POST",
      "/api/auth/register",
      {
        username: "admin",
        email: username + "@example.test",
        password,
        acceptedTerms: true,
      },
      false,
    );
    assert.equal(r.status, 400);
    console.log("PASS reserved username rejected");
    r = await api(
      "POST",
      "/api/auth/register",
      {
        username,
        email: username + "@example.test",
        password,
        acceptedTerms: true,
      },
      false,
    );
    assert.equal(r.status, 200);
    token = r.data.token;
    console.log("Disposable account " + username);
    const deck = await api("POST", "/api/decks", {
      name: "Disposable live audit deck",
      format: "modern",
    });
    assert.equal(deck.status, 201);
    driver = await buildDriver();
    await login();
    await navigate("http://localhost:4200/account");
    const input = await visible(".pe-field input");
    await input.clear();
    await input.sendKeys("Live audit profile");
    await (await visible(".pe-savebar .pe-btn-primary")).click();
    await driver.wait(
      async () =>
        (await driver.findElements(By.css(".pe-savebar"))).length === 0,
      10000,
    );
    await shot("desktop-profile");
    await navigate("http://localhost:4200/u/" + username);
    await visible(".up-page .up-tabs");
    assert(
      (await driver.findElement(By.css("body")).getText()).includes(
        "Live audit profile",
      ),
    );
    console.log("PASS profile saved and publicly rendered");
    await navigate("http://localhost:4200/deck/" + deck.data.id);
    await (await visible(".publish-btn")).click();
    await (
      await visible(".publish-modal-textarea")
    ).sendKeys("Disposable live audit description");
    await (await visible(".publish-modal-submit")).click();
    await driver.wait(
      async () => /\/forum\/[a-f0-9-]+/.test(await driver.getCurrentUrl()),
      15000,
    );
    const postId = (await driver.getCurrentUrl()).split("/").pop();
    const forum = new ForumPage(driver);
    await forum.addComment("Live audit comment");
    assert.deepEqual(await forum.getCommentTexts(), ["Live audit comment"]);
    await forum.clickEditComment(0);
    await forum.submitEditComment("Edited live audit comment");
    assert.deepEqual(await forum.getCommentTexts(), [
      "Edited live audit comment",
    ]);
    console.log("PASS UI publish and comment create/edit");
    await shot("desktop-forum");
    r = await api("POST", "/api/forum", {
      deckId: deck.data.id,
      description: "Republished live audit",
    });
    assert.equal(r.status, 200);
    assert.equal(r.data.commentCount, 1);
    console.log("PASS republish preserves comment count");
    await driver.quit();
    driver = await buildDriver({
      device: {
        os: "android",
        width: 375,
        height: 667,
        dpr: 1,
        ua: "Mozilla/5.0 (Linux; Android 13) AppleWebKit/537.36 Chrome/130.0.0.0 Mobile Safari/537.36",
      },
    });
    await login();
    await navigate("http://localhost:4200/forum/" + postId);
    await visible(".header-title");
    await shot("mobile-forum");
    assert(
      await driver.executeScript(
        "return document.documentElement.scrollWidth <= window.innerWidth",
      ),
      "forum horizontal overflow",
    );
    await navigate("http://localhost:4200/u/" + username);
    await visible(".up-page .up-tabs");
    await shot("mobile-profile");
    assert(
      await driver.executeScript(
        "return document.documentElement.scrollWidth <= window.innerWidth",
      ),
      "profile horizontal overflow",
    );
    await navigate("http://localhost:4200/community/players");
    await visible(".pl-grid");
    await (await visible(".pf-search-wrap input")).sendKeys(username);
    await driver.wait(
      async () => (await driver.findElements(By.css(".pl-card"))).length === 1,
      10000,
    );
    await shot("mobile-players");
    console.log(
      "PASS mobile forum/profile/directory and no horizontal overflow",
    );
  } catch (err) {
    if (driver) await shot("failure");
    console.error(err.stack);
    process.exitCode = 1;
  } finally {
    if (driver) await driver.quit();
    if (token) {
      const r = await api("DELETE", "/api/account", { password });
      console.log("Disposable account cleanup HTTP " + r.status);
      if (r.status !== 204) console.log(JSON.stringify(r.data));
      if (r.status !== 204) process.exitCode = 1;
    }
  }
})();

const fs = require("fs");
const path = require("path");
const assert = require("assert");
const root = path.resolve(__dirname, "../../../mtg-client/e2e");
const { By, until } = require(
  path.join(root, "node_modules/selenium-webdriver"),
);
const { buildDriver } = require(path.join(root, "helpers/driver"));
const BasePage = require(path.join(root, "pages/BasePage"));
const devices = require(path.join(root, "devices"));
const out = path.resolve(__dirname, "../../audit-live-ai");
fs.mkdirSync(out, { recursive: true });
function fixtures() {
  localStorage.clear();
  sessionStorage.clear();
  localStorage.setItem(
    "auth_token",
    "x." +
      btoa(JSON.stringify({ sub: "audit-fixture", exp: 4102444800 })) +
      ".x",
  );
  localStorage.setItem("auth_username", "Audit Fixture");
  window.__requests = [];
  const card = {
    oracleId: "audit-forest",
    cardId: "audit-forest",
    name: "Forest",
    manaCost: "",
    manaValue: 0,
    cardTypes: ["Land"],
    subtypes: ["Forest"],
    supertypes: ["Basic"],
    oracleText: "{T}: Add {G}.",
    colorIdentity: ["G"],
    keywords: [],
    imageUriNormal: null,
    imageUriArtCrop: null,
  };
  const deck = {
    id: "audit-fixture",
    name: "Audit fixture deck",
    format: "commander",
    commanderOracleId: null,
    tags: [],
    notes: null,
    coverUri: null,
    isPublished: false,
    createdAt: "2026-01-01",
    updatedAt: "2026-01-01",
    cards: [
      {
        id: "audit-row",
        oracleId: card.oracleId,
        scryfallId: null,
        quantity: 3,
        quantityFoil: 0,
        quantityEtched: 0,
        board: "main",
        notes: null,
        addedAt: "2026-01-01",
        cardDetails: card,
      },
    ],
  };
  const swaps = {
    swaps: [
      { out: "Forest", in: "Llanowar Elves", why: "A low-cost ramp fixture." },
    ],
    rejectedByReason: {},
    deckSizeBefore: 3,
    deckSizeAfter: 3,
  };
  const open = XMLHttpRequest.prototype.open,
    send = XMLHttpRequest.prototype.send;
  XMLHttpRequest.prototype.open = function (method, url, ...rest) {
    this.__method = method;
    this.__url = String(url);
    return open.call(this, method, url, ...rest);
  };
  XMLHttpRequest.prototype.send = function (body) {
    if (!this.__url.includes("/api/")) return send.call(this, body);
    window.__requests.push({
      url: this.__url,
      method: this.__method,
      body: body ? JSON.parse(body) : null,
    });
    let result = [];
    if (this.__url.includes("/api/profile/me"))
      result = {
        profile: { username: "Audit Fixture", displayName: "Audit Fixture" },
      };
    else if (this.__url.includes("/preferences")) result = {};
    else if (this.__url.includes("/ai-refine")) result = swaps;
    else if (this.__url.includes("/ai-build/apply"))
      result = {
        cardsAdded: 1,
        sideboardAdded: 0,
        maybeboardAdded: 0,
        cardsSkipped: 0,
        mainTarget: 99,
        mainShortfall: 98,
        skippedByReason: {},
      };
    else if (this.__url.match(/\/api\/decks\/audit-fixture(?:-2)?(?:\?|$)/))
      result = { ...deck, id: this.__url.includes("audit-fixture-2") ? "audit-fixture-2" : deck.id };
    else if (this.__url.includes("/api/cards/audit-forest")) result = card;
    else if (this.__url.endsWith("/api/decks")) result = [deck];
    const value =
      this.responseType === "json" ? result : JSON.stringify(result);
    Object.defineProperties(this, {
      status: { value: 200, configurable: true },
      statusText: { value: "OK", configurable: true },
      response: { value, configurable: true },
      responseText: { value: JSON.stringify(result), configurable: true },
      readyState: { value: 4, configurable: true },
    });
    this.getAllResponseHeaders = () => "Content-Type: application/json\r\n";
    setTimeout(() => {
      this.dispatchEvent(new Event("load"));
      this.dispatchEvent(new Event("loadend"));
    }, 10);
  };
  window.fetch = async (input, init = {}) => {
    const url = typeof input === "string" ? input : input.url;
    const method = init.method || "GET";
    const body = init.body ? JSON.parse(init.body) : null;
    window.__requests.push({ url, method, body });
    let result = [];
    if (url.includes("/api/profile/me"))
      result = {
        profile: { username: "Audit Fixture", displayName: "Audit Fixture" },
      };
    else if (url.includes("/preferences")) result = {};
    else if (url.includes("/ai-refine")) result = swaps;
    else if (url.includes("/ai-build/apply"))
      result = {
        cardsAdded: 1,
        sideboardAdded: 0,
        maybeboardAdded: 0,
        cardsSkipped: 0,
        mainTarget: 99,
        mainShortfall: 98,
        skippedByReason: {},
      };
    else if (url.match(/\/api\/decks\/audit-fixture(?:-2)?(?:\?|$)/)) result = { ...deck, id: url.includes("audit-fixture-2") ? "audit-fixture-2" : deck.id };
    else if (url.includes("/api/cards/audit-forest")) result = card;
    else if (url.endsWith("/api/decks")) result = [deck];
    return new Response(JSON.stringify(result), {
      status: 200,
      headers: { "Content-Type": "application/json" },
    });
  };
}
async function click(d, selector) {
  const el = await d.wait(until.elementLocated(By.css(selector)), 15000);
  await d.executeScript('arguments[0].scrollIntoView({block:"center"})', el);
  await el.click();
}
(async () => {
  const results = [];
  for (const id of ["board-desktop", "iphone-se"]) {
    const d = await buildDriver({
      device: devices.find((x) => x.id === id),
      dpr: 1,
    });
    try {
      await d.sendDevToolsCommand("Page.addScriptToEvaluateOnNewDocument", {
        source: "(" + fixtures.toString() + ")();",
      });
      await new BasePage(d).navigate("/deck/build");
      await d.wait(until.elementLocated(By.css("app-ai-deck-builder")), 30000);
      const skips = await d.findElements(By.css(".intro-skip"));
      if (skips.length) await skips[0].click();
      await d.executeScript(
        `const c=ng.getComponent(document.querySelector('app-ai-deck-builder')); c.deckId='audit-fixture';c.step='plan';c.plan={priceRange:'budget',commanderOracleId:'audit-commander',commanderName:'Budget fixture commander',cards:[{oracleId:'audit-forest',scryfallId:null,name:'Forest',manaCost:'',typeLine:'Basic Land — Forest',imageUriArtCrop:null,board:'main',quantity:1}],mainTarget:99,mainShortfall:98,cardsSkipped:0,skippedByReason:{},assessment:{verdict:'Deterministic budget preview fixture.',findings:[],facts:{cards:1,lands:1,ramp:0,draw:0,interaction:0,interactionOnCreatures:0,other:0,creatures:0,creaturePercentOfNonland:0,manaSources:1,averageManaValue:0,colorSources:[{color:'G',count:1}]}}};ng.applyChanges(c);`,
      );
      await d.sleep(400);
      fs.writeFileSync(
        path.join(out, id + "-build-preview.png"),
        await d.takeScreenshot(),
        "base64",
      );
      const save = await d.findElement(
        By.xpath("//button[contains(.,'Save to deck')]"),
      );
      await d.executeScript(
        'arguments[0].scrollIntoView({block:"center"})',
        save,
      );
      await save.click();
      await d.wait(
        async () =>
          await d.executeScript(
            "return window.__requests.some(r=>r.url.includes('ai-build/apply'))",
          ),
        10000,
      );
      const build = await d.executeScript(
        "return window.__requests.find(r=>r.url.includes('ai-build/apply')).body",
      );
      assert.equal(build.priceRange, "budget");
      await new BasePage(d).navigate("/deck/audit-fixture");
      await d.wait(until.elementLocated(By.css("app-deck-detail")), 15000);
      const intro = await d.findElements(By.css(".intro-skip"));
      if (intro.length) await intro[0].click();
      await d.wait(
        async () =>
          await d.executeScript(
            "return !!ng.getComponent(document.querySelector('app-deck-detail')).currentDeck",
          ),
        10000,
      );
      await d.executeScript(
        "const c=ng.getComponent(document.querySelector('app-deck-detail'));c.toggleRefinePanel();ng.applyChanges(c);",
      );
      await d.wait(
        until.elementLocated(By.css("app-deck-refine-panel")),
        10000,
      );
      await d.executeScript(
        "const c=ng.getComponent(document.querySelector('app-deck-refine-panel'));c.priceRange='budget';ng.applyChanges(c);",
      );
      await click(d, ".rf-intro .rf-btn-primary");
      await d.wait(until.elementLocated(By.css(".rf-swap")), 10000);
      await d.executeScript(
        "const c=ng.getComponent(document.querySelector('app-deck-refine-panel'));c.priceRange='any';ng.applyChanges(c);",
      );
      fs.writeFileSync(
        path.join(out, id + "-refine-preview.png"),
        await d.takeScreenshot(),
        "base64",
      );
      await click(d, ".rf-actions .rf-btn-primary");
      await d.wait(
        async () =>
          await d.executeScript(
            "return window.__requests.some(r=>r.url.includes('ai-refine/apply'))",
          ),
        10000,
      );
      const refine = await d.executeScript(
        "return window.__requests.find(r=>r.url.includes('ai-refine/apply')).body",
      );
      assert.equal(refine.priceRange, "budget");
      assert.equal(refine.swaps.length, 1);
      const geometry = await d.executeScript(
        "return {width:innerWidth,documentWidth:document.documentElement.scrollWidth}",
      );
      await d.executeScript("const c=ng.getComponent(document.querySelector('app-deck-detail'));window.__previousDetail=c;c.zone.run(()=>c.router.navigate(['/deck','audit-fixture-2']));");
      await d.wait(async()=>await d.executeScript("const e=document.querySelector('app-deck-detail');const c=e&&ng.getComponent(e);return !!c&&c!==window.__previousDetail&&c.deckId==='audit-fixture-2'&&c.currentDeck?.id==='audit-fixture-2';"),15000);
      const routeReset = await d.executeScript("const c=ng.getComponent(document.querySelector('app-deck-detail'));return !c.showRefinePanel && !c.selectedCard;");
      assert.equal(routeReset,true);
      results.push({ device: id, build, refine, geometry, routeReset });
    } catch (e) {
      console.error(
        JSON.stringify(
          await d.executeScript(
            "return {requests:window.__requests,text:document.body.innerText}",
          ),
        ),
      );
      console.error(await d.manage().logs().get("browser"));
      fs.writeFileSync(
        path.join(out, id + "-failure.png"),
        await d.takeScreenshot(),
        "base64",
      );
      throw e;
    } finally {
      await d.quit();
    }
  }
  fs.writeFileSync(
    path.join(out, "results.json"),
    JSON.stringify(results, null, 2),
  );
  console.log(JSON.stringify(results, null, 2));
})().catch((e) => {
  console.error(e);
  process.exitCode = 1;
});

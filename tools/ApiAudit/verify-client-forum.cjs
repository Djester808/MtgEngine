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
const out = path.resolve("audit-live-client-forum");
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

async function request(method,url,body,status=200){const r=await api(method,url,body);assert.equal(r.status,status,method+' '+url+' HTTP '+r.status);return r.data;}
async function route(url){await driver.executeScript('window.history.pushState({}, "", arguments[0]); window.dispatchEvent(new PopStateEvent("popstate"));',url);}
async function clickText(text){const button=await driver.wait(until.elementLocated(By.xpath('//button[normalize-space(.)="'+text+'"]')),15000);await driver.executeScript('arguments[0].scrollIntoView({block:"center"})',button);await button.click();}
(async()=>{try{
 const ready=await fetch('http://localhost:4200');assert(ready.ok,'Client server unavailable before seeding');
 const session=await request('POST','/api/auth/register',{username,email:username+'@example.test',password,acceptedTerms:true});token=session.token;console.log('Disposable verifier account '+username);
 const tymna=await request('GET','/api/cards/by-name?name='+encodeURIComponent('Tymna the Weaver'));
 const thrasios=await request('GET','/api/cards/by-name?name='+encodeURIComponent('Thrasios, Triton Hero'));
 const ring=await request('GET','/api/cards/by-name?name=Sol%20Ring');
 const printings=await request('GET','/api/cards/'+ring.oracleId+'/printings');
 const etched=printings.find(p=>p.prices?.usdEtched != null);assert(etched,'Sol Ring etched printing unavailable');
 const deck=await request('POST','/api/decks',{name:username+' Partner deck',format:'commander',commanderOracleId:tymna.oracleId,secondaryCommanderOracleId:thrasios.oracleId},201);
 await request('POST','/api/decks/'+deck.id+'/cards',{oracleId:ring.oracleId,scryfallId:etched.scryfallId,quantity:0,quantityFoil:0,quantityEtched:2,board:'main'},201);
 const post=await request('POST','/api/forum',{deckId:deck.id,description:'Disposable pagination and copy verification'});
 for(let i=0;i<51;i++)await request('POST','/api/forum/'+post.id+'/comments',{content:'Pagination comment '+i});
 const posts=[post];
 for(let i=0;i<51;i++){const d=await request('POST','/api/decks',{name:username+' Page deck '+i,format:'modern'},201);posts.push(await request('POST','/api/forum',{deckId:d.id}));}
 console.log('Seeded52posts/51comments and etched partner deck through real API');
 driver=await buildDriver();await login();await navigate('http://localhost:4200/community/forum');await visible('.post-card');
 assert.equal((await driver.findElements(By.css('.post-card'))).length,50);await clickText('Load more decks');await driver.wait(async()=>(await driver.findElements(By.css('.post-card'))).length>50,15000);console.log('PASS forum list loads beyond50');await shot('desktop-list-page2');
 await navigate('http://localhost:4200/forum/'+post.id);await visible('.header-title');assert.equal((await driver.findElements(By.css('.comments-list .comment'))).length,50);await clickText('Load more comments');await driver.wait(async()=>(await driver.findElements(By.css('.comments-list .comment'))).length===51,15000);await shot('desktop-comments-page2');console.log('PASS comments load beyond50');
 await route('/forum/'+posts[1].id);await driver.wait(async()=>(await (await visible('.header-title')).getText()).includes('Page deck 0'),15000);assert.equal((await driver.findElements(By.css('.comments-list .comment'))).length,0);console.log('PASS reused forum route clears prior comments');
 await route('/forum/'+post.id);await driver.wait(async()=>(await (await visible('.header-title')).getText()).includes('Partner deck'),15000);await (await visible('.copy-deck-btn')).click();await driver.wait(async()=>/\/deck\/[a-f0-9-]+/.test(await driver.getCurrentUrl()),30000);
 const copyId=(await driver.getCurrentUrl()).split('/').pop(),copy=await request('GET','/api/decks/'+copyId);
 assert.equal(copy.commanderOracleId,tymna.oracleId);assert.equal(copy.secondaryCommanderOracleId,thrasios.oracleId);assert(copy.cards.some(c=>c.oracleId===ring.oracleId&&c.quantityEtched===2&&c.scryfallId===etched.scryfallId));console.log('PASS browser copy preserves both commanders, etched count and printing');await shot('desktop-copied-partner');
 const players=await request('GET','/api/users'),other=players.find(p=>p.username!==username);
 if(other){await navigate('http://localhost:4200/u/'+username);await visible('.up-page .up-tabs');await route('/u/'+encodeURIComponent(other.username));await driver.wait(async()=>(await driver.findElement(By.css('.up-page')).getText()).includes(other.displayName||other.username),15000);await route('/u/'+username);await driver.wait(async()=>(await driver.findElement(By.css('.up-page')).getText()).includes(username),15000);console.log('PASS reused public profile route switches users');}
 await driver.quit();driver=await buildDriver({device:{os:'android',width:375,height:667,dpr:1,ua:'Mozilla/5.0 (Linux; Android 13) AppleWebKit/537.36 Chrome/130.0.0.0 Mobile Safari/537.36'}});
 await navigate('http://localhost:4200/forum/'+post.id);await visible('.header-title');await clickText('Load more comments');await driver.wait(async()=>(await driver.findElements(By.css('.comments-list .comment'))).length===51,15000);assert(await driver.executeScript('return document.documentElement.scrollWidth<=window.innerWidth'));await shot('mobile-comments-page2');
 await navigate('http://localhost:4200/community/forum');await visible('.post-card');await clickText('Load more decks');await driver.wait(async()=>(await driver.findElements(By.css('.post-card'))).length>50,15000);assert(await driver.executeScript('return document.documentElement.scrollWidth<=window.innerWidth'));await shot('mobile-list-page2');console.log('PASS375px pagination without document overflow');
}catch(error){if(driver)await shot('failure');console.error(error.stack);process.exitCode=1;}finally{if(driver)await driver.quit();if(token){const r=await api('DELETE','/api/account',{password});console.log('Disposable cleanup HTTP '+r.status);if(r.status!==204)process.exitCode=1;}}})();

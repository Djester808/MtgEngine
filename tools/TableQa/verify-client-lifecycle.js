const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const client = path.resolve(__dirname, '../../../mtg-client');
const { buildDriver } = require(path.join(client, 'e2e/helpers/driver'));
const { By, Key, until } = require(path.join(client, 'e2e/node_modules/selenium-webdriver'));
const origin = process.env.QA_ORIGIN || 'http://localhost:4340';
const output = path.join(__dirname, 'screenshots/client-lifecycle');
const observeSockets = `
  window.qaGameSockets = [];
  const NativeWebSocket = window.WebSocket;
  window.WebSocket = class extends NativeWebSocket {
    constructor(...args) {
      super(...args);
      if (String(args[0]).includes('/hubs/game')) {
        const record = { opened: false, closed: false };
        window.qaGameSockets.push(record);
        this.addEventListener('open', () => record.opened = true);
        this.addEventListener('close', () => record.closed = true);
      }
    }
  };
`;
async function ready(driver) {
  await driver.wait(() => driver.executeScript(`
    document.querySelector('.brand-intro:not(.is-exiting) .intro-skip')?.click();
    const shell = document.querySelector('.app-shell');
    return !!shell && !shell.hasAttribute('inert');
  `), 15000);
}
async function capture(driver, name) {
  fs.writeFileSync(path.join(output, name + '.png'), await driver.takeScreenshot(), 'base64');
}
async function sockets(driver) {
  return driver.executeScript('return window.qaGameSockets');
}
async function main() {
  fs.mkdirSync(output, { recursive: true });
  const { gameId } = await fetch(origin + '/api/qa/table').then((r) => r.json());
  assert(gameId, 'QA fixture must return a saved game');
  const driver = await buildDriver();
  let step = 'initializing';
  const mark = (name) => { step = name; console.log('STEP ' + name); };
  try {
    await driver.sendDevToolsCommand('Page.addScriptToEvaluateOnNewDocument', { source: observeSockets });
    await driver.get(origin + '/login');
    await driver.executeScript(`localStorage.setItem('auth_token','qa0'); localStorage.setItem('auth_username','qa0');`);
    mark('open board');
    await driver.get(origin + '/game/' + gameId);
    await ready(driver);
    await driver.wait(until.elementLocated(By.css('.self-table .life')), 20000);
    await driver.wait(async () => (await sockets(driver)).filter((s) => s.opened && !s.closed).length === 1, 10000);
    mark('board ready');
    const before = await sockets(driver);
    await capture(driver, 'board-before');
    await driver.findElement(By.css('.board-settings-button')).click();
    const lobbyLink = await driver.findElement(By.css('.board-settings-panel a[href="/lobby"]'));
    mark('leave board via lobby link');
    await lobbyLink.click();
    await driver.wait(() => driver.getCurrentUrl().then((url) => /\/(lobby|play)$/.test(new URL(url).pathname)), 10000);
    assert(await driver.findElement(By.css('.gl-page')).isDisplayed(), 'Lobby must be visible');
    mark('wait retired socket close');
    await driver.wait(async () => (await sockets(driver)).every((s) => s.closed), 10000, 'Game socket survived board destruction');
    assert.equal((await driver.findElements(By.css('.self-table'))).length, 0);
    await capture(driver, 'lobby-after-leaving');
    mark('return to board via history');
    await driver.navigate().back();
    await driver.wait(until.elementLocated(By.css('.self-table .life')), 20000);
    await driver.wait(async () => (await sockets(driver)).filter((s) => s.opened && !s.closed).length === 1, 10000);
    const returned = await sockets(driver);
    assert.equal(returned.length, before.length + 1, 'SPA return creates exactly one fresh socket');
    assert(returned.slice(0, -1).every((s) => s.closed), 'Retired sockets stay closed');
    await capture(driver, 'board-returned');

    mark('open life counter');
    await driver.get(origin + '/tools/life-counter');
    await ready(driver);
    mark('life setup ready');
    await driver.wait(until.elementLocated(By.css('.setup .primary-btn')), 10000);
    await driver.findElement(By.xpath('//fieldset[legend[normalize-space()="Players"]]//button[normalize-space()="2"]')).click();
    await driver.findElement(By.xpath('//fieldset[legend[normalize-space()="Starting life"]]//button[normalize-space()="40"]')).click();
    await driver.findElement(By.css('.setup .primary-btn')).click();
    await driver.wait(async () => (await driver.findElements(By.css('app-player-panel'))).length === 2, 5000);
    mark('life table interactions');
    const life = async () => Number(await driver.findElement(By.css('app-player-panel .life')).getText());
    assert.equal(await life(), 40);
    await driver.findElement(By.css('app-player-panel .tap-up')).sendKeys(Key.SPACE);
    await driver.wait(async () => await life() === 41, 3000);
    await driver.findElement(By.xpath('//button[normalize-space()="Undo"]')).click();
    await driver.wait(async () => await life() === 40, 3000);
    for (const [width, height, name] of [[1440,900,'desktop'],[375,667,'mobile']]) {
      mark('life viewport ' + name);
      await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: width < 900 });
      await driver.findElement(By.css('app-player-panel .tap-down')).sendKeys(Key.SPACE);
      const expected = name === 'desktop' ? 39 : 38;
      await driver.wait(async () => await life() === expected, 3000);
      assert(await driver.executeScript('return document.documentElement.scrollWidth <= innerWidth'), name + ' document overflows');
      await driver.sleep(300); // Let the responsive navbar transition settle before capture.
      await capture(driver, 'life-counter-' + name);
    }
    await driver.navigate().refresh();
    await ready(driver);
    await driver.wait(async () => await life() === 38, 5000);
    await capture(driver, 'life-counter-mobile-reloaded');
    console.log(JSON.stringify({ gameId, socketsBefore: before.length, socketsAfterReturn: returned.length,
      result: 'Board→lobby closes old socket; SPA return opens one fresh socket. Life counter2 seats, gain, undo, desktop/mobile loss, persistence and no horizontal overflow passed.', output }));
  } catch (error) {
    console.error('FAILED STEP', step);
    console.error(JSON.stringify(await driver.executeScript(`return {
      url: location.href, sockets: window.qaGameSockets,
      text: document.body.innerText.slice(0,6000),
      dialogs: [...document.querySelectorAll('dialog')].map(d => ({open:d.open, class:d.className}))
    }`)));
    await capture(driver, 'failure');
    throw error;
  } finally { await driver.quit(); }
}
main().catch((error) => { console.error(error); process.exitCode = 1; });
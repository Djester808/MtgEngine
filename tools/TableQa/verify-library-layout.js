const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const client = path.resolve(__dirname, '../../../mtg-client');
const { buildDriver } = require(path.join(client, 'e2e/helpers/driver'));
const { By, until } = require(path.join(client, 'e2e/node_modules/selenium-webdriver'));
const expected = Object.freeze({ version: 1, libraryPanel: Object.freeze({ left: 20, top: 30, width: 400, height: 300 }) });
(async () => {
  const output = fs.mkdtempSync(path.join(__dirname, 'layout-acceptance-'));
  const valid = path.join(output, 'valid.json'), invalid = path.join(output, 'invalid.json');
  fs.writeFileSync(valid, JSON.stringify(expected));
  fs.writeFileSync(invalid, '{broken');
  const driver = await buildDriver();
  let hub;
  try {
    await driver.sendDevToolsCommand('Page.setDownloadBehavior', { behavior: 'allow', downloadPath: output });
    await driver.get('http://localhost:4340/login');
    await driver.executeScript('localStorage.setItem("auth_token","qa0"); localStorage.setItem("auth_username","qa0");');
    await driver.get('http://localhost:4340/settings');
    const search = await driver.wait(until.elementLocated(By.id('settings-search')), 20000);
    const skip = await driver.findElements(By.css('.intro-skip'));
    if (skip.length) await skip[0].click();
    await driver.wait(async () => !(await driver.findElements(By.css('.brand-intro'))).length, 12000);
    await search.sendKeys('library layout');
    const input = await driver.findElement(By.id('library-layout-file'));
    await input.sendKeys(valid);
    await driver.wait(async () => (await driver.findElement(By.css('.settings-results [role="status"]')).getText()).includes('restored'), 10000);
    const stored = () => driver.executeScript('return localStorage.getItem("compleat.ui.preferences")');
    assert.deepEqual(JSON.parse(await stored()).libraryPanel, expected.libraryPanel);
    await driver.findElement(By.xpath('//button[normalize-space(.)="Export library layout"]')).click();
    const downloaded = path.join(output, 'library-layout.json');
    await driver.wait(() => fs.existsSync(downloaded), 10000);
    assert.deepEqual(JSON.parse(fs.readFileSync(downloaded, 'utf8')), expected);
    const before = await stored();
    await input.sendKeys(invalid);
    await driver.wait(async () => (await driver.findElement(By.css('.settings-results [role="status"]')).getText()).includes('not changed'), 10000);
    assert.equal(await stored(), before);
    for (const [width, height, label] of [[1280,800,'desktop'],[375,667,'portrait'],[667,375,'landscape']]) {
      await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: label !== 'desktop' });
      await driver.executeScript('arguments[0].scrollIntoView({block: "center"})', input);
      assert(await driver.executeScript('return document.documentElement.scrollWidth <= innerWidth'));
      const labelBox = await driver.findElement(By.css('label[for="library-layout-file"]')).getRect();
      const exportBox = await driver.findElement(By.xpath('//button[normalize-space(.)="Export library layout"]')).getRect();
      assert(labelBox.y >= exportBox.y + exportBox.height, 'Import label must occupy its own row');
      fs.writeFileSync(path.join(output, label + '.png'), await driver.takeScreenshot(), 'base64');
    }
    await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width: 1280, height: 800, deviceScaleFactor: 1, mobile: false });
    const { gameId } = await fetch('http://localhost:4340/api/qa/table').then(r => r.json());
    const signalR = require(path.join(client, 'node_modules/@microsoft/signalr'));
    hub = new signalR.HubConnectionBuilder().withUrl('http://localhost:4340/hubs/game', { accessTokenFactory: () => 'qa0' }).configureLogging(signalR.LogLevel.Error).build();
    const refusals = [];
    hub.on('Refused', value => refusals.push(value));
    hub.on('ViewUpdated', () => {}); hub.on('ChoiceRequested', () => {}); hub.on('Log', () => {});
    await hub.start(); await hub.invoke('Join', gameId);
    await hub.invoke('Command', gameId, { kind: 'manualLibraryLook', requestId: require('node:crypto').randomUUID() });
    assert.deepEqual(refusals, []);
    await driver.get('http://localhost:4340/game/' + gameId);
    const panel = await driver.wait(until.elementLocated(By.css('[data-active-inspection]')), 20000);
    const gameSkip = await driver.findElements(By.css('.intro-skip'));
    if(gameSkip.length) await gameSkip[0].click();
    await driver.wait(async () => !(await driver.findElements(By.css('.brand-intro'))).length, 12000);
    await driver.wait(async () => {
      const r = await panel.getRect();
      return Math.abs(r.x - expected.libraryPanel.left) < 1 && Math.abs(r.y - expected.libraryPanel.top) < 1 &&
        Math.abs(r.width - expected.libraryPanel.width) < 1 && Math.abs(r.height - expected.libraryPanel.height) < 1;
    }, 10000, 'Imported layout must set the actual inspection geometry');
    fs.writeFileSync(path.join(output, 'imported-game-panel.png'), await driver.takeScreenshot(), 'base64');
    console.log(JSON.stringify({ output, result: 'Valid import, exact export, invalid-file preservation, viewport bounds and imported game-panel geometry passed.' }));
  } finally { if(hub) await hub.stop(); await driver.quit(); }
})().catch(error => { console.error(error); process.exitCode = 1; });

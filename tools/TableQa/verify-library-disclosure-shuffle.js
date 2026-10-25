const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const client = path.resolve(__dirname, '../../../mtg-client');
const { buildDriver } = require(path.join(client, 'e2e/helpers/driver'));
const { By, until } = require(path.join(client, 'e2e/node_modules/selenium-webdriver'));
const signalR = require(path.join(client, 'node_modules/@microsoft/signalr'));
const origin = 'http://localhost:4340';
const privateLook = process.argv.includes('--private');
const allLibraries = process.argv.includes('--all-libraries');
const roleGeometry = process.argv.includes('--role-geometry');
const raisePanels = process.argv.includes('--raise-panels');
assert(!raisePanels || allLibraries, 'Panel activation requires all-owner disclosure');
assert(!(raisePanels && roleGeometry), 'Overlap and separated geometry are independent fixtures');
assert(!roleGeometry || allLibraries, 'Role geometry requires all-owner disclosure');
assert(!(allLibraries && privateLook), 'All-library disclosure probe is public');
const disclosedCount = allLibraries ? 8 : 2;
const phasingDetail = process.argv.includes('--phasing-detail');
const phasing = process.argv.includes('--phasing') || phasingDetail;

(async () => {
  const drivers = [], hubs = [], views = [], revisions = [0, 0, 0, 0], refusals = [];
  const { gameId } = await fetch(origin + '/api/qa/table').then(r => r.json());
  const read = (route, seat = 0) => fetch(origin + route,
    { headers: { Authorization: 'Bearer qa' + seat } }).then(r => r.json());
  const dismiss = async driver => {
    const skip = await driver.findElements(By.css('.intro-skip'));
    if (skip.length) await skip[0].click();
    await driver.wait(async () => !(await driver.findElements(By.css('.brand-intro'))).length, 12000);
  };
  const broadcast = async action => {
    const previous = [...revisions];
    await action();
    await drivers[0].wait(() => revisions.every((n, seat) => n > previous[seat]), 10000);
  };
  const capture = async (phase, seat) => {
    const driver = drivers[seat];
    for (const [width, height, name] of [[1280, 800, 'desktop'], [375, 667, 'portrait'], [667, 375, 'landscape']]) {
      await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: name !== 'desktop' });
      assert(await driver.executeScript('return document.documentElement.scrollWidth <= innerWidth'));
      if (name === 'portrait') assert(await driver.findElement(By.css('.portrait-notice')).isDisplayed());
      fs.writeFileSync(path.join(__dirname, `library-${phasingDetail ? 'phasing-detail' : phasing ? 'phasing' : privateLook ? 'private' : 'disclosure'}-shuffle-${phase}-seat-${seat}-${name}.png`), await driver.takeScreenshot(), 'base64');
    }
    await driver.sendDevToolsCommand('Emulation.clearDeviceMetricsOverride', {});
  };
  try {
    for (let seat = 0; seat < 4; seat++) {
      const hub = new signalR.HubConnectionBuilder().withUrl(origin + '/hubs/game', { accessTokenFactory: () => 'qa' + seat }).configureLogging(signalR.LogLevel.Error).build();
      hubs.push(hub);
      hub.on('ViewUpdated', view => { views[seat] = view; revisions[seat]++; });
      hub.on('Refused', reason => refusals.push(reason));
      hub.on('ChoiceRequested', () => {});
      hub.on('Log', () => {});
      await hub.start();
      await hub.invoke('Join', gameId);
      const driver = await buildDriver();
      drivers.push(driver);
      const url = 'http://localhost:' + (4340 + seat);
      await driver.get(url + '/login');
      await driver.executeScript('localStorage.setItem("auth_token",arguments[0]);localStorage.setItem("auth_username",arguments[0]);', 'qa' + seat);
      if (roleGeometry || raisePanels) await driver.executeScript('localStorage.setItem("compleat.ui.preferences",JSON.stringify(arguments[0]))', {
        libraryPanel: { left: 20, top: 80, width: 300, height: 200 },
        opponentLibraryPanel: { left: raisePanels ? 200 : 500, top: 80, width: 300, height: 200 },
      });
      await driver.get(url + '/game/' + gameId);
      await driver.wait(until.elementLocated(By.css('.self-table .life')), 20000);
      await dismiss(driver);
    }
    if (phasing) {
      const top = views[0].players[0].lookableTopCard;
      assert(top);
      for (let seat = 1; seat < 4; seat++) assert.equal(views[seat].players[0].lookableTopCard, null);
      assert((await read('/api/games/' + gameId)).legal.castable.some(id => id.value === top.id.value));
      const provider = views[0].battlefield.find(card => card.owner.value === 0 && card.oracleId === 'relic');
      assert(provider);
      await drivers[0].findElement(By.css('.self-table [data-zone="library"]')).click();
      const card = await drivers[0].wait(until.elementLocated(By.css('.viewer-panel .zone-card')), 10000);
      if (phasingDetail) {
        await card.click();
        await drivers[0].wait(until.elementLocated(By.css('.card-preview-panel app-card-modal')), 10000);
      } else {
        await drivers[0].actions().contextClick(card).perform();
        await drivers[0].wait(until.elementLocated(By.css('.card-options')), 10000);
      }
      await capture('before', 0);
      await broadcast(() => hubs[0].invoke('Command', gameId, { kind: 'activate', source: provider.id.value, abilityIndex: 0 }));
      for (let n = 0; views[0].stack.length && n < 8; n++)
        await broadcast(() => hubs[views[0].priorityHolder.value].invoke('Command', gameId, { kind: 'pass' }));
      assert.equal(views[0].stack.length, 0);
      for (let seat = 0; seat < 4; seat++) {
        assert.equal(views[seat].players[0].lookableTopCard, null);
        assert.equal(views[seat].players[0].libraryCount, views[0].players[0].libraryCount);
      }
      assert(!(await read('/api/games/' + gameId)).legal.castable.some(id => id.value === top.id.value));
      await drivers[0].wait(async () => !(await drivers[0].findElements(By.css('.viewer-panel .zone-card, .card-options, .card-preview-panel'))).length, 10000);
      await capture('revoked', 0);
      for (let seat = 0; seat < 4; seat++) {
        await drivers[seat].navigate().refresh();
        await drivers[seat].wait(until.elementLocated(By.css('.self-table .life')), 20000);
        await dismiss(drivers[seat]);
        assert.deepEqual((await read('/api/games/' + gameId, seat)).view, views[seat]);
      }
      assert.deepEqual(refusals, []);
      console.log(JSON.stringify({ gameId, revisions, phasingDetail, result: 'Four-seat phased provider revokes private top, legal cast, open menu/detail and viewer card; all-seat reload passed.' }));
      return;
    }
    const before = await read('/api/qa/library-events');
    assert.equal(views[0].stack.length, 1);
    for (let n = 0; !views[0].pending && n < 4; n++)
      await broadcast(() => hubs[views[0].priorityHolder.value].invoke('Command', gameId, { kind: 'pass' }));
    for (const phase of ['revealed', 'shuffled']) {
      assert.equal(views[0].pending.kind, 'may');
      const pendingId = views[0].pending.id;
      for (let seat = 0; seat < 4; seat++) {
        const canInspect = phase === 'revealed' && (!privateLook || seat === 0);
        assert.equal(views[seat].inspectedLibraryCards.length, canInspect ? disclosedCount : 0);
        if (privateLook) assert.equal(views[seat].lastReveal?.cards?.length ?? 0, 0);
        else assert.equal(views[seat].lastReveal.cards.length, disclosedCount);
        if (seat) assert.equal(views[seat].pending.options.length, 0);
        await drivers[seat].navigate().refresh();
        await drivers[seat].wait(until.elementLocated(By.css('.self-table .life')), 20000);
        await dismiss(drivers[seat]);
        assert.deepEqual((await read('/api/games/' + gameId, seat)).view, views[seat]);
        const active = await drivers[seat].findElements(By.css('.library-reveal[data-active-inspection]'));
        assert.equal(active.length, canInspect ? allLibraries ? 4 : 1 : 0);
        if (canInspect && allLibraries) {
          if (raisePanels && seat === 0) {
            const driver = drivers[seat];
            await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width: 1280, height: 800, deviceScaleFactor: 1, mobile: false });
            const originalView = structuredClone(views[seat]);
            const originalEvents = await read('/api/qa/library-events');
            const clickAt = async (x, y) => driver.actions().move({ x, y, origin: 'viewport' }).click().perform();
            const overlapOwner = () => driver.executeScript("return document.elementFromPoint(250,250)?.closest('[data-library-owner]')?.getAttribute('data-library-owner')");
            await clickAt(50,250);
            fs.writeFileSync(path.join(__dirname, 'library-panel-raise-own.png'), await driver.takeScreenshot(), 'base64');
            assert.equal(await overlapOwner(), 'p0', 'Clicking exposed own panel background must raise it above overlapping opponents');
            const exposedOwner = await driver.executeScript("return document.elementFromPoint(450,250)?.closest('[data-library-owner]')?.getAttribute('data-library-owner')");
            assert(['p1','p2','p3'].includes(exposedOwner), 'The exposed click target must be an opponent');
            await clickAt(450,250);
            fs.writeFileSync(path.join(__dirname, 'library-panel-raise-opponent.png'), await driver.takeScreenshot(), 'base64');
            assert.notEqual(await overlapOwner(), 'p0', 'Clicking exposed opponent background must raise that panel');
            assert.equal(await overlapOwner(), exposedOwner, 'The exposed clicked opponent must be the raised owner');
            await driver.findElement(By.css('[data-library-owner="p0"] [data-panel-handle]')).sendKeys('\uE004');
            assert.equal(await overlapOwner(), 'p0', 'Keyboard focus within own panel must bring it forward');
            await clickAt(750,450);
            assert.equal(await overlapOwner(), 'p0', 'Raised window must remain in front after focus moves to the battlefield');
            fs.writeFileSync(path.join(__dirname, 'library-panel-retained-order.png'), await driver.takeScreenshot(), 'base64');
            assert.deepEqual(views[seat], originalView, 'Panel activation must not change the pending game view');
            assert.deepEqual(await read('/api/qa/library-events'), originalEvents, 'Panel activation must not emit game events');
          }
          const owners = new Set();
          // Last DOM panel is frontmost; collapsing it exposes the next owner.
          for (let index=active.length-1; index>=0; index--) {
            const panel=active[index];
            assert(await panel.getAttribute('open'));
            owners.add(await panel.getAttribute('data-library-owner'));
            assert.equal((await panel.findElements(By.css('.inspection-cards button'))).length,2);
            for(const [width,height,label] of [[1280,800,'desktop'],[667,375,'landscape']]) {
              await drivers[seat].sendDevToolsCommand('Emulation.setDeviceMetricsOverride',{width,height,deviceScaleFactor:1,mobile:width<900});
              assert(await panel.findElement(By.css('summary')).isDisplayed());              if(roleGeometry && label==='desktop') {
                const own=(await panel.getAttribute('data-library-owner'))==='p'+seat;
                await drivers[seat].wait(async()=> {
                  const box=await panel.getRect();
                  return Math.abs(box.x-(own?20:500))<1 && Math.abs(box.y-80)<1 && Math.abs(box.width-300)<1 && Math.abs(box.height-200)<1;
                },10000,'Each viewer must restore own versus opponent geometry');
              }
              if(seat===0) fs.writeFileSync(path.join(__dirname,`library-all-disclosure-owner-${index}-${label}.png`),await drivers[seat].takeScreenshot(),'base64');
            }
            await panel.findElement(By.css('summary')).click();
            assert.equal(await panel.getAttribute('open'),null);
          }
          assert.equal(owners.size,4);
          await drivers[seat].sendDevToolsCommand('Emulation.clearDeviceMetricsOverride',{});
          assert.deepEqual(views[0].pending.id,pendingId);
          continue;
        }
        if (canInspect) assert(await active[0].getAttribute('open'), 'Authorized disclosure opens after reload');
        if (seat < 2) await capture(phase, seat);
        if (canInspect) await drivers[seat].findElement(By.css('.library-reveal[data-active-inspection] summary')).click();
      }
      assert.deepEqual(views[0].pending.id, pendingId);
      const buttons = await drivers[0].findElements(By.css('.choice-option button.choice'));
      const labels = await Promise.all(buttons.map(button => button.getText()));
      const no = labels.findIndex(label => /^no$/i.test(label.trim()));
      assert(no >= 0, 'May choice must offer No');
      await broadcast(() => buttons[no].click());
    }
    const completed = await read('/api/qa/library-events');
    assert.equal(completed.shuffles - before.shuffles, allLibraries ? 4 : 1);
    assert.equal(completed.reveals - before.reveals, privateLook ? 0 : 1);
    for (let seat = 0; seat < 4; seat++) {
      assert.equal(views[seat].pending, null);
      assert.equal(views[seat].inspectedLibraryCards.length, 0);
      await drivers[seat].navigate().refresh();
      await drivers[seat].wait(until.elementLocated(By.css('.self-table .life')), 20000);
      await dismiss(drivers[seat]);
      assert.deepEqual((await read('/api/games/' + gameId, seat)).view, views[seat]);
    }
    assert.deepEqual(await read('/api/qa/library-events'), completed);
    assert.deepEqual(refusals, []);
    console.log(JSON.stringify({ gameId, privateLook, allLibraries, revisions, result: 'Four-seat inspection, shuffle revocation, pending reload, responsive captures and final reload passed.' }));
  } catch (error) {
    if (phasing) console.error(JSON.stringify({ refusals, revisions, pending: views[0]?.pending, stack: views[0]?.stack }));
    throw error;
  } finally {
    for (const driver of drivers) await driver.quit();
    for (const hub of hubs) await hub.stop();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });

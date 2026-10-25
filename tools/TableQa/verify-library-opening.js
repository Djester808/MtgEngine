const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const client = path.resolve(__dirname, '../../../mtg-client');
const { buildDriver } = require(path.join(client, 'e2e/helpers/driver'));
const { By, until } = require(path.join(client, 'e2e/node_modules/selenium-webdriver'));
const signalR = require(path.join(client, 'node_modules/@microsoft/signalr'));
const origin = 'http://localhost:4340';
const seats = process.argv.includes('--four') ? 4 : 2;
const choiceBusy = process.argv.includes('--choice-busy');
let pendingChecked = false;

(async () => {
  const drivers = [], hubs = [], views = [], revisions = Array(seats).fill(0), refusals = [];
  const { gameId } = await fetch(origin + '/api/qa/table').then(r => r.json());
  const read = (route, seat = 0) => fetch(origin + route,
    { headers: { Authorization: 'Bearer qa' + seat } }).then(r => r.json());
  const ready = async driver => {
    await driver.wait(until.elementLocated(By.css('.self-table .life')), 20000);
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
    for (const [width, height, name] of [[1280, 800, 'desktop'], [375, 667, 'portrait'], [667, 375, 'landscape']]) {
      await drivers[seat].sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: name !== 'desktop' });
      assert(await drivers[seat].executeScript('return document.documentElement.scrollWidth <= innerWidth'));
      fs.writeFileSync(path.join(__dirname, `library-opening-${seats === 4 ? 'four' : 'two'}-${phase}-seat-${seat}-${name}.png`), await drivers[seat].takeScreenshot(), 'base64');
    }
    await drivers[seat].sendDevToolsCommand('Emulation.clearDeviceMetricsOverride', {});
  };
  const decide = async key => {
    const seat = views[0].pending.player.value;
    const selector = `app-choice-controls [aria-keyshortcuts="${key}"]`;
    await drivers[seat].wait(until.elementLocated(By.css(selector)), 15000);
    await broadcast(async () => {
      await drivers[seat].findElement(By.css(selector)).click();
      if (choiceBusy && seat === 0 && !pendingChecked) {
        await drivers[seat].wait(() => drivers[seat].executeScript('return window.libraryBusyProbe.held'), 10000);
        const options = await drivers[seat].findElements(By.css('app-choice-controls button.choice'));
        assert.equal(options.length, 1);
        assert.equal(await options[0].getAttribute('aria-keyshortcuts'), key);
        assert.equal(await options[0].isEnabled(), false);
        await capture('sending', seat);
        assert.equal(await drivers[seat].executeScript('return window.libraryBusyProbe.commands.length'), 1);
        pendingChecked = true;
        await drivers[seat].executeScript('window.libraryBusyProbe.release()');
      }
    });
  };
  try {
    for (let seat = 0; seat < seats; seat++) {
      const hub = new signalR.HubConnectionBuilder().withUrl(origin + '/hubs/game', { accessTokenFactory: () => 'qa' + seat }).configureLogging(signalR.LogLevel.Error).build();
      hubs.push(hub);
      hub.on('ViewUpdated', view => { views[seat] = view; revisions[seat]++; });
      hub.on('Refused', reason => refusals.push(reason));
      hub.on('ChoiceRequested', choice => assert.equal(choice.player.value, seat));
      hub.on('Log', () => {});
      await hub.start(); await hub.invoke('Join', gameId);
      const driver = await buildDriver(); drivers.push(driver);
      if (choiceBusy && seat === 0) await driver.sendDevToolsCommand('Page.addScriptToEvaluateOnNewDocument', {
        source: 'window.libraryBusyTarget = "answer"; window.libraryBusyOnceKey = "qa-opening-sending";\n' + fs.readFileSync(path.join(__dirname, 'hold-library-command.js'), 'utf8'),
      });
      const url = 'http://localhost:' + (4340 + seat);
      await driver.get(url + '/login');
      await driver.executeScript('localStorage.setItem("auth_token",arguments[0]);localStorage.setItem("auth_username",arguments[0]);', 'qa' + seat);
      await driver.get(url + '/game/' + gameId); await ready(driver);
    }
    assert.equal(views[0].players.length, seats);
    assert.equal(views[0].turnNumber, 0);
    for (const player of views[0].players) {
      assert.equal(player.libraryCount, 92, 'Commander opening library contains 99 minus seven drawn cards');
      assert.equal(player.handCount, 7);
      assert.equal(player.command.length, 1, 'The commander remains outside the opening library');
      assert.equal(player.libraryCount + player.handCount + player.command.length, 100);
    }
    const first = views[0].pending.player.value, other = 1 - first;
    const observers = Array.from({ length: seats }, (_, seat) => seat).filter(seat => seat !== first);
    const keptHands = new Map(observers.map(seat => [seat, views[seat].hand.map(card => card.id.value)]));
    await decide('3');
    assert.equal(views[0].pending.player.value, other);
    for (let n = 1; n < seats; n++) await decide('4');
    if (seats === 4) {
      assert.equal(views[first].pending.kind, 'mulligan');
      assert.equal(views[first].hand.length, 7, 'First multiplayer mulligan is free');
      assert.equal(views[first].players.find(p => p.id.value === first).libraryCount, 92);
      await decide('3');
    }
    assert.equal(views[first].pending.kind, 'mulligan-bottom');
    assert.equal(views[first].pending.min, 1);
    assert.equal(views[first].pending.max, 1);
    assert.equal(views[first].hand.length, 7);
    for (const seat of observers) assert.equal(views[seat].pending.options.length, 0);
    const pending = views[first].pending.id;
    for (let seat = 0; seat < seats; seat++) {
      await drivers[seat].navigate().refresh(); await ready(drivers[seat]);
      assert.deepEqual((await read('/api/games/' + gameId, seat)).view, views[seat]);
      if (seat !== first) assert.equal(views[seat].pending.options.length, 0);
      await capture('bottom-reload', seat);
    }
    assert.deepEqual(views[first].pending.id, pending);
    const options = await drivers[first].findElements(By.css('app-choice-controls .choice'));
    assert.equal(options.length, 7);
    await broadcast(() => options[0].click());
    assert.equal(views[first].hand.length, 6);
    assert.equal(views[first].players.find(player => player.id.value === first).libraryCount, 93,
      'Bottoming after a paid mulligan restores one card to the 99-card library');
    for (const seat of observers) assert.deepEqual(views[seat].hand.map(card => card.id.value), keptHands.get(seat));
    assert.equal(views[0].pending.kind, 'mulligan');
    await decide('4');
    assert.equal(views[0].pending, null);
    assert.equal(views[0].turnNumber, 1);
    const counts = await read('/api/qa/library-events');
    for (let n = 0; n < seats * 5 && views[0].step !== 'PrecombatMain'; n++) {
      await broadcast(() => hubs[views[0].priorityHolder.value].invoke('Command', gameId, { kind: 'pass' }));
      if (views[0].step === 'PrecombatMain') break;
    }
    assert.equal(views[0].turnNumber, 1);
    assert.equal(views[first].hand.length, seats === 4 ? 7 : 6, 'First draw occurs only in multiplayer');
    assert.equal(views[0].step, 'PrecombatMain');
    assert.equal((await read('/api/qa/library-events')).draws, counts.draws + (seats === 4 ? 1 : 0));
    for (let seat = 0; seat < seats; seat++) {
      await drivers[seat].navigate().refresh(); await ready(drivers[seat]);
      assert.deepEqual((await read('/api/games/' + gameId, seat)).view, views[seat]);
      if (seats === 4) {
        await drivers[seat].findElement(By.xpath("//button[normalize-space(.)='Table']")).click();
        await drivers[seat].wait(until.elementLocated(By.css('app-table-seat [data-player-id="p3"]')), 10000);
      }
      for (const player of views[seat].players) {
        const panel = await drivers[seat].findElement(By.css(`app-table-seat [data-player-id="p${player.id.value}"]`));
        const summaries = await panel.findElements(By.css('.player-panel p'));
        if (seats === 2 || player.id.value === seat) assert(summaries.length > 0);
        const pile = await panel.findElement(By.css('[data-zone="library"] .zone-label b')).getText();
        if (summaries.length) assert.equal(await summaries[0].getText(), `Hand ${player.handCount} · Library ${player.libraryCount}`);
        assert.equal(Number(pile), player.libraryCount);
      }
      await capture('completed', seat);
    }
    assert.deepEqual(refusals, []);
    console.log(JSON.stringify({ gameId, seats, revisions, step: views[0].step, result: 'Commander-size opening, mulligan bottoming after reload, privacy, kept-hand preservation and player-count-specific first draw passed.' }));
  } finally {
    for (const driver of drivers) await driver.quit();
    for (const hub of hubs) await hub.stop();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });

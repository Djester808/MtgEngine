const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { setInspectionExpanded } = require('./library-inspection-ui');
const client = path.resolve(__dirname, '../../../mtg-client');
const { buildDriver } = require(path.join(client, 'e2e/helpers/driver'));
const { By, Key, until } = require(path.join(client, 'e2e/node_modules/selenium-webdriver'));
const signalR = require(path.join(client, 'node_modules/@microsoft/signalr'));
const origin = 'http://localhost:4340';
const reloadResponse = process.argv.includes('--reload-response');
const dropStop = process.argv.includes('--drop-stop');
const lostResponse = process.argv.includes('--lost-response') || reloadResponse;
const shuffle = process.argv.includes('--shuffle');
const draw = process.argv.includes('--draw');
const searchBattlefield = process.argv.includes('--search-battlefield');
const zeroMatch = process.argv.includes('--zero-match');
const partialSearch = process.argv.includes('--partial-search');
const choiceSkip = process.argv.includes('--choice-skip');
const choiceBusy = process.argv.includes('--choice-busy') || choiceSkip;
const zeroPick = process.argv.includes('--zero-pick') || choiceSkip;
const pointerProbe = process.argv.includes('--pointer-probe');
const panelProbe = process.argv.includes('--panel-probe');
const searchPanelProbe = process.argv.includes('--search-panel-probe');
const withdrawalProbe = process.argv.includes('--withdrawal-probe');
const invalidSearch = process.argv.includes('--invalid-search');
const destinationControls = process.argv.includes('--destination-controls');
const opponentLibrary = process.argv.includes('--opponent-library');
const rejectBusy = process.argv.includes('--reject-busy');
const busyRequest = process.argv.includes('--busy-request') || rejectBusy;
const smallLibrary = process.argv.includes('--small-library');
const emptyDraw = process.argv.includes('--empty-draw');
const search = process.argv.includes('--search') || searchBattlefield;
if (searchPanelProbe && !searchBattlefield)
    throw Error('--search-panel-probe requires --search-battlefield');
if (choiceBusy && (!search || busyRequest || lostResponse))
    throw Error('--choice-busy requires search without other transport probes');
if (withdrawalProbe && (!search || searchBattlefield || zeroMatch || zeroPick))
    throw Error('--withdrawal-probe requires a normal --search fixture');
if (busyRequest && (!search || lostResponse))
    throw Error('--busy-request requires search without response loss');
if (zeroPick && (!search || searchBattlefield || zeroMatch))
    throw Error('--zero-pick requires --search without --search-battlefield or --zero-match');
if (partialSearch && !searchBattlefield)
    throw Error('--partial-search requires --search-battlefield');
const searchCount = zeroMatch || zeroPick ? 0 : partialSearch ? 1 : searchBattlefield || pointerProbe || withdrawalProbe ? 2 : 1;
const reposition = process.argv.includes('--reposition');
const rejectArrange = process.argv.includes('--reject-arrange');
const arrange = process.argv.includes('--arrange') || rejectArrange;
const interveningReposition = process.argv.includes('--intervening-reposition');
const interveningArrange = process.argv.includes('--intervening-arrange') || interveningReposition;
if (interveningArrange && (!arrange || !lostResponse))
    throw Error('--intervening-arrange requires --arrange and response loss');
const placement = process.argv.includes('--bottom') ? 'bottom' : 'top';
const touch = process.argv.includes('--touch');
const revokeInspection = process.argv.includes('--revoke-inspection');
const modeGuard = process.argv.includes('--mode-guard');
const brokenArtwork = process.argv.includes('--broken-artwork');
const topLand = process.argv.includes('--top-land');
const topPlay = process.argv.includes('--top-play') || topLand;
const waitReady = process.argv.includes('--wait-ready');
if (waitReady && !topPlay)
    throw Error('--wait-ready requires --top-play or --top-land');
const detailFocus = process.argv.includes('--detail-focus') || brokenArtwork;
const look = process.argv.includes('--look') || reposition || arrange || detailFocus || topPlay;
if (panelProbe && !look)
    throw Error('--panel-probe requires --look');
if (lostResponse && (!draw && !shuffle && !search && !look))
    throw Error('--lost-response requires --draw, --shuffle, --search, --look or --reposition/--arrange');
if (dropStop && (!lostResponse || !look || reposition || arrange))
    throw Error('--drop-stop requires plain --look and a response-loss flag');
if (!(shuffle || draw || look || search)) throw Error('Choose --shuffle, --draw, --look, --search or --arrange.');
const seatCount = process.argv.includes('--four') ? 4 : 2;
const replacement = shuffle || draw;
const capture = (arrange ? 'library-arrange' + (touch ? '-touch' : '') + (revokeInspection ? '-revocation' : '') + '-live' : searchBattlefield ? 'library-search-battlefield-live' : search ? 'library-search-manual-live' : look ? 'library-look-live' : draw ? 'library-draw-live' : shuffle ? 'library-shuffle-live' : replacement ? 'library-replacement-live' : 'library-live') + (seatCount === 4 ? '-four' : '');
async function configureSearch(driver) {
    await driver.findElement(By.css('.library-search summary')).click();
    await driver.findElement(By.css('[name="searchType"] option[value="Land"]')).click();
    if (zeroMatch)
        await driver.findElement(By.css('[name="searchSubtypes"]')).sendKeys('NonexistentQaSubtype');
    if (pointerProbe || withdrawalProbe) {
        const count = await driver.findElement(By.css('[name="searchCount"]'));
        await count.clear();
        await count.sendKeys('2');
    }
    if (searchBattlefield) {
        const count = await driver.findElement(By.css('[name="searchCount"]'));
        await count.clear();
        await count.sendKeys('2');
        await driver.findElement(By.css('[name="searchDestination"] option[value="Battlefield"]')).click();
        await driver.findElement(By.css('[name="searchSupertype"] option[value="Basic"]')).click();
        await driver.findElement(By.css('[name="searchSubtypes"]')).sendKeys('Forest');
        await driver.findElement(By.css('[name="searchTapped"]')).click();
        await driver.findElement(By.css('[name="searchReveal"]')).click();
        await driver.findElement(By.css('[name="searchShuffle"]')).click();
    }
}
async function inspectedCard(driver, inspected, id) {
    const selected = inspected.find(card => card.id.value === id);
    assert(selected, 'The selected card must be in the current inspection');
    const sameName = inspected.filter(card => card.name === selected.name);
    const occurrence = sameName.findIndex(card => card.id.value === id);
    const matching = [];
    const cards = await driver.findElements(By.css('.library-reveal[data-active-inspection] .inspection-cards button'));
    for (const candidate of cards)
        if ((await candidate.getText()).trim() === selected.name)
            matching.push(candidate);
    assert.equal(matching.length, sameName.length);
    return matching[occurrence];
}
async function checkWaitingInspection(driver, inspected, id, capture) {
    await dismissIntro(driver);
    await setInspectionExpanded(driver, true);
    assert((await driver.findElement(By.css('.library-reveal[data-active-inspection] [role="status"]')).getText()).includes('Waiting for game connection'));
    assert.equal((await driver.findElements(By.css('.library-reveal[data-active-inspection] .inspection-group'))).length, 0);
    await (await inspectedCard(driver, inspected, id)).click();
    const menu = await driver.findElement(By.css('.card-options'));
    assert.deepEqual((await menu.getText()).split('\n'), ['Card details', 'Close'], 'Before the hub view arrives, inspection must offer details but no game mutation');
    fs.writeFileSync(path.join(__dirname, capture + '.png'), await driver.takeScreenshot(), 'base64');
    await driver.findElement(By.xpath("//button[@role='menuitem' and normalize-space(.)='Close']")).click();
    await driver.findElement(By.css('.library-reveal[data-active-inspection] summary')).click();
}
async function dismissIntro(driver) {
    const skip = await driver.findElements(By.css('.intro-skip'));
    if (skip.length)
        await skip[0].click();
    await driver.wait(async () => !(await driver.findElements(By.css('.brand-intro'))).length, 12000);
}
async function tapElement(driver, element) {
    await driver.executeScript('arguments[0].scrollIntoView({block:"nearest"})', element);
    const point = await driver.executeScript('const r=arguments[0].getBoundingClientRect(); return {x:r.x+r.width/2,y:r.y+r.height/2}', element);
    await driver.sendDevToolsCommand('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [point] });
    await driver.sendDevToolsCommand('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
}
(async () => {
    const drivers = [], hubs = [], views = [], revisions = Array(seatCount).fill(0), refusals = [];
    const { gameId } = await fetch(origin + '/api/qa/table').then(r => r.json());
    const snapshot = async () => fetch(origin + '/api/games/' + gameId, { headers: { Authorization: 'Bearer qa0' } }).then(r => r.json());
    try {
        for (let seat = 0; seat < seatCount; seat++) {
            const hub = new signalR.HubConnectionBuilder().withUrl(origin + '/hubs/game', { accessTokenFactory: () => 'qa' + seat }).configureLogging(signalR.LogLevel.Error).build();
            hubs.push(hub);
            hub.on('ViewUpdated', view => { views[seat] = view; revisions[seat]++; });
            hub.on('Refused', value => refusals.push(value));
            hub.on('ChoiceRequested', () => { });
            hub.on('Log', () => { });
            await hub.start();
            await hub.invoke('Join', gameId);
            const driver = await buildDriver();
            drivers.push(driver);
            if (choiceBusy && seat === 0)
                await driver.sendDevToolsCommand('Page.addScriptToEvaluateOnNewDocument', {
                    source: 'window.libraryBusyTarget = "answer";\n' + fs.readFileSync(path.join(__dirname, 'hold-library-command.js'), 'utf8'),
                });
            if (withdrawalProbe && seat === 0)
                await driver.sendDevToolsCommand('Page.addScriptToEvaluateOnNewDocument', {
                    source: fs.readFileSync(path.join(__dirname, 'library-choice-withdrawal-probe.js'), 'utf8'),
                });
            if (topPlay && seat === 0)
                await driver.sendDevToolsCommand('Page.addScriptToEvaluateOnNewDocument', {
                    source: `window.libraryCastProbe = []; window.libraryCastReady = false; const send = WebSocket.prototype.send;
          WebSocket.prototype.send = function(data) {
            if (!this.libraryCastObserved) {
              this.libraryCastObserved = true;
              this.addEventListener('message', event => {
                if (typeof event.data === 'string') for (const frame of event.data.split(String.fromCharCode(30))) {
                  try { if (JSON.parse(frame).target === 'ViewUpdated') window.libraryCastReady = true; } catch {}
                }
              });
            }
            if (typeof data === 'string') for (const frame of data.split(String.fromCharCode(30))) {
              try { const message = JSON.parse(frame); if (message.target === 'Command')
                window.libraryCastProbe.push(message.arguments[1]); } catch {}
            }
            return send.call(this, data);
          };`,
                });
            if (waitReady && seat === 0)
                await driver.sendDevToolsCommand('Page.addScriptToEvaluateOnNewDocument', {
                    source: fs.readFileSync(path.join(__dirname, 'hold-library-ready.js'), 'utf8'),
                });
            if ((busyRequest || rejectArrange) && seat === 0)
                await driver.sendDevToolsCommand('Page.addScriptToEvaluateOnNewDocument', {
                    source: (rejectArrange ? 'window.libraryBusyTarget = "manualLibraryArrange";\n' : '')
                        + fs.readFileSync(path.join(__dirname, 'hold-library-command.js'), 'utf8'),
                });
            if (lostResponse && seat === 0)
                await driver.sendDevToolsCommand('Page.addScriptToEvaluateOnNewDocument', {
                    source: (arrange ? 'window.libraryDeliveryTarget = "manualLibraryArrange";\n' : reposition ? 'window.libraryDeliveryTarget = "manualLibraryReposition";\n' : dropStop ? 'window.libraryDeliveryTarget = "manualLibraryStopLooking";\n' : '')
                        + fs.readFileSync(path.join(__dirname, 'drop-library-response.js'), 'utf8'),
                });
            const url = 'http://localhost:' + (4340 + seat);
            await driver.get(url + '/login');
            await driver.executeScript('localStorage.setItem("auth_token",arguments[0]);localStorage.setItem("auth_username",arguments[0]);', 'qa' + seat);
            await driver.get(url + '/game/' + gameId);
            await driver.wait(until.elementLocated(By.css('.self-table .life')), 20000);
            await dismissIntro(driver);
        }
        const before = await snapshot();
        const libraryEvents = async () => fetch(origin + '/api/qa/library-events', { headers: { Authorization: 'Bearer qa0' } }).then(r => r.json());
        const beforeEvents = search || lostResponse ? await libraryEvents() : null;
        const libraryOrders = async () => fetch(origin + '/api/qa/library-orders', { headers: { Authorization: 'Bearer qa0' } }).then(r => r.json());
        const beforeOrders = lostResponse && look ? await libraryOrders() : null;
        assert.equal(before.view.players.length, seatCount);
        const driver = drivers[0];
        const initialRevisions = [...revisions];
        if (opponentLibrary) {
            const events = await libraryEvents();
            await driver.findElement(By.css('.opponent-boards .zone-pile[data-zone="library"]')).click();
            await driver.wait(until.elementLocated(By.css('.viewer-title')), 10000);
            assert(!(await driver.findElement(By.css('.viewer-title')).getText()).startsWith('Your'));
            for (const [width, height, label] of [[1440, 900, 'desktop'], [375, 667, 'portrait'], [667, 375, 'landscape']]) {
                await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: width < 900 });
                assert.equal((await driver.findElements(By.css('.library-actions, .library-search, .library-order-editor, .viewer-card-name'))).length, 0);
                assert((await driver.findElement(By.css('.viewer-empty')).getText()).toLowerCase().includes('card identities are private'));
                const close = await driver.findElement(By.css('.viewer-close'));
                assert(await close.isDisplayed());
                assert(await driver.executeScript('const r=arguments[0].getBoundingClientRect(); return r.left>=0 && r.right<=innerWidth && r.top>=0 && r.bottom<=innerHeight && document.documentElement.scrollWidth<=innerWidth', close));
                fs.writeFileSync(path.join(__dirname, `library-opponent-controls-${label}.png`), await driver.takeScreenshot(), 'base64');
                assert.deepEqual(await libraryEvents(), events);
                assert.deepEqual(revisions, initialRevisions);
            }
            await driver.sendDevToolsCommand('Emulation.clearDeviceMetricsOverride', {});
            await driver.findElement(By.css('.viewer-close')).click();
        }
        if (smallLibrary) {
            const count = before.view.players[0].libraryCount;
            assert([0, 1].includes(count));
            await driver.findElement(By.css('.self-table .zone-pile[data-zone="library"]')).click();
            assert((await driver.findElement(By.css('.viewer-count')).getText()).startsWith(`${count} cards`));
            await driver.findElement(By.css('.library-look')).click();
            await driver.wait(() => views[0].manualLibraryLookActive, 10000);
            assert.equal((views[0].inspectedLibraryCards ?? []).length, count);
            for (let seat = 1; seat < seatCount; seat++)
                assert.equal((views[seat].inspectedLibraryCards ?? []).length, 0);
            await driver.findElement(By.css('.self-table .zone-pile[data-zone="library"]')).click();
            const arrangeButton = await driver.wait(until.elementLocated(By.css('.library-arrange')), 10000);
            assert.equal(await arrangeButton.isEnabled(), false);
            for (const [width, height, label] of [[1440, 900, 'desktop'], [375, 667, 'portrait'], [667, 375, 'landscape']]) {
                await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: width < 900 });
                await driver.executeScript('arguments[0].scrollIntoView({block:"nearest"})', arrangeButton);
                assert(await driver.executeScript('return document.documentElement.scrollWidth <= innerWidth'));
                fs.writeFileSync(path.join(__dirname, `library-${count}-cards-${label}.png`), await driver.takeScreenshot(), 'base64');
            }
            await driver.sendDevToolsCommand('Emulation.clearDeviceMetricsOverride', {});
            await driver.findElement(By.css('.library-look')).click();
            await driver.wait(() => !views[0].manualLibraryLookActive, 10000);
            assert.equal((views[0].inspectedLibraryCards ?? []).length, 0);
            if (emptyDraw) {
                if (count === 1) {
                    const beforeLastCard = await libraryEvents();
                    const originalHand = views[0].hand;
                    await driver.findElement(By.css('.self-table .zone-pile[data-zone="library"]')).click();
                    await driver.findElement(By.css('.library-draw')).click();
                    await driver.wait(() => views.every(view => view.players[0].libraryCount === 0), 10000);
                    assert(views.every(view => !view.players[0].hasLost));
                    assert.equal(views[0].hand.length, originalHand.length + 1);
                    const card = views[0].hand.find(item => !originalHand.some(old => old.id.value === item.id.value));
                    assert(card?.oracleId);
                    const checkPrivate = value => {
                        if (!value || typeof value !== 'object')
                            return;
                        assert(!(value.id?.value === card.id.value && value.oracleId), 'Last drawn card must remain private');
                        for (const child of Object.values(value))
                            checkPrivate(child);
                    };
                    for (const view of views.slice(1))
                        checkPrivate(view);
                    const afterLastCard = await libraryEvents();
                    assert.equal(afterLastCard.draws - beforeLastCard.draws, 1);
                    assert.equal(afterLastCard.emptyDraws, beforeLastCard.emptyDraws);
                    const saved = await snapshot();
                    await driver.navigate().refresh();
                    await driver.wait(until.elementLocated(By.css('.self-table .life')), 20000);
                    await dismissIntro(driver);
                    assert.deepEqual(await snapshot(), saved);
                    fs.writeFileSync(path.join(__dirname, 'library-last-card-drawn.png'), await driver.takeScreenshot(), 'base64');
                }
                const eventsBeforeDraw = await libraryEvents();
                await driver.findElement(By.css('.self-table .zone-pile[data-zone="library"]')).click();
                await driver.findElement(By.css('.library-draw')).click();
                await driver.wait(() => views.every(view => view.players[0].hasLost), 10000);
                const eventsAfterDraw = await libraryEvents();
                assert.equal(eventsAfterDraw.emptyDraws - eventsBeforeDraw.emptyDraws, 1);
                assert.equal(eventsAfterDraw.draws, eventsBeforeDraw.draws);
                for (const view of views) {
                    assert(view.players.slice(1).every(player => !player.hasLost));
                }
                assert.equal(views[0].manualLibraryActionsAllowed, false);
                assert(views.every(view => view.priorityHolder.value === 1), 'Priority must pass to the next surviving seat');
                await driver.wait(until.elementLocated(By.css('.self-table .eliminated')), 10000);
                assert.equal(await driver.findElement(By.css('.self-table .eliminated')).getText(), 'Eliminated');
                assert.equal(await driver.findElement(By.css('.prompt-title')).getText(), 'You are eliminated');
                for (const [width, height, label] of [[1440, 900, 'desktop'], [375, 667, 'portrait'], [667, 375, 'landscape']]) {
                    await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: width < 900 });
                    fs.writeFileSync(path.join(__dirname, `library-empty-draw-${label}.png`), await driver.takeScreenshot(), 'base64');
                }
                await driver.sendDevToolsCommand('Emulation.clearDeviceMetricsOverride', {});
                for (const seat of [1, 2, 3]) {
                    assert.equal(views[0].priorityHolder.value, seat);
                    const prior = [...revisions];
                    await hubs[seat].invoke('Command', gameId, { kind: 'pass' });
                    await driver.wait(() => revisions.every((revision, index) => revision > prior[index]), 10000);
                }
                assert(views.every(view => view.step === 'BeginningOfCombat' && view.priorityHolder.value === 1));
            }
            for (let seat = 0; seat < seatCount; seat++) {
                const saved = await fetch(origin + '/api/games/' + gameId, { headers: { Authorization: 'Bearer qa' + seat } }).then(r => r.json());
                await drivers[seat].navigate().refresh();
                await drivers[seat].wait(until.elementLocated(By.css('.self-table .life')), 20000);
                await dismissIntro(drivers[seat]);
                const reloaded = await fetch(origin + '/api/games/' + gameId, { headers: { Authorization: 'Bearer qa' + seat } }).then(r => r.json());
                assert.deepEqual(reloaded, saved);
                assert.equal(reloaded.view.players[0].libraryCount, emptyDraw ? 0 : count);
            }
            assert.deepEqual(refusals, []);
            console.log(JSON.stringify({ gameId, count, seatCount, result: 'Small-library counts, disabled arrangement, private inspection, stop and all-seat reload passed.' }));
            return;
        }
        {
            const action = search ? '.library-search-submit' : look ? '.library-look' : draw ? '.library-draw' : '.library-shuffle';
            await driver.findElement(By.css('.self-table .zone-pile[data-zone="library"]')).click();
            await driver.wait(until.elementLocated(By.css(action)), 10000);
            if (search) {
                await configureSearch(driver);
                if (destinationControls) {
                    for (const [width, height, label] of [[1440, 900, 'desktop'], [375, 667, 'portrait'], [667, 375, 'landscape']]) {
                        await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: width < 900 });
                        for (const destination of ['Hand', 'Battlefield', 'Library', 'Graveyard', 'Exile']) {
                            const select = await driver.findElement(By.css('[name="searchDestination"]'));
                            await select.findElement(By.css(`option[value="${destination}"]`)).click();
                            await driver.wait(async () => (await driver.findElements(By.css('[name="searchPlacement"]'))).length === Number(destination === 'Library')
                                && (await driver.findElements(By.css('[name="searchTapped"]'))).length === Number(destination === 'Battlefield'), 5000);
                            const target = destination === 'Library' ? '[name="searchPlacement"]' : destination === 'Battlefield' ? '[name="searchTapped"]' : '[name="searchDestination"]';
                            const control = await driver.findElement(By.css(target));
                            await driver.executeScript('arguments[0].scrollIntoView({block:"center"})', control);
                            assert(await control.isDisplayed());
                            assert(await driver.executeScript('const r=arguments[0].getBoundingClientRect(); return r.left>=0 && r.right<=innerWidth && r.top>=0 && r.bottom<=innerHeight && document.documentElement.scrollWidth<=innerWidth', control));
                            fs.writeFileSync(path.join(__dirname, `library-search-destination-${destination}-${label}.png`), await driver.takeScreenshot(), 'base64');
                            assert.deepEqual(await libraryEvents(), beforeEvents);
                            assert.deepEqual(revisions, initialRevisions);
                        }
                    }
                    await driver.sendDevToolsCommand('Emulation.clearDeviceMetricsOverride', {});
                    await driver.findElement(By.css('[name="searchDestination"] option[value="' + (searchBattlefield ? 'Battlefield' : 'Hand') + '"]')).click();
                }
                if (invalidSearch) {
                    for (const [width, height, label] of [[1440, 900, 'desktop'], [375, 667, 'portrait'], [667, 375, 'landscape']]) {
                        await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: width < 900 });
                        const count = await driver.findElement(By.css('[name="searchCount"]'));
                        for (const [value, kind] of [['', 'empty'], ['-1', 'negative'], ['0.5', 'fractional']]) {
                            await count.sendKeys(Key.chord(Key.CONTROL, 'a'), Key.BACK_SPACE);
                            if (value)
                                await count.sendKeys(value);
                            await driver.executeScript('arguments[0].blur()', count);
                            await driver.executeScript('document.querySelector(".library-search-submit").scrollIntoView({block:"nearest"})');
                            fs.writeFileSync(path.join(__dirname, `library-search-invalid-${kind}-${label}.png`), await driver.takeScreenshot(), 'base64');
                            assert.equal(await driver.findElement(By.css(action)).isEnabled(), false, `${kind} search count must disable submit at ${label}`);
                            await driver.executeScript('document.querySelector(".library-search-submit").click()');
                            assert.deepEqual(await libraryEvents(), beforeEvents, 'Invalid input must not change library events');
                            assert.deepEqual(revisions, initialRevisions, 'Invalid input must not broadcast a command result');
                        }
                        await count.clear();
                        await count.sendKeys(String(searchCount));
                    }
                    await driver.sendDevToolsCommand('Emulation.clearDeviceMetricsOverride', {});
                }
                for (const [width, height, label] of [[375, 667, 'portrait'], [667, 375, 'landscape']]) {
                    await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: true });
                    await driver.executeScript('document.querySelector(".viewer-panel").scrollTop = 0');
                    assert(await driver.executeScript('return document.documentElement.scrollWidth <= innerWidth'));
                    fs.writeFileSync(path.join(__dirname, capture + '-form-' + label + '.png'), await driver.takeScreenshot(), 'base64');
                    await driver.executeScript('document.querySelector(".library-search-submit").scrollIntoView({block:"nearest"})');
                    assert(await driver.executeScript('const r=document.querySelector(".library-search-submit").getBoundingClientRect(); return r.height >= 44 && r.left >= 0 && r.right <= innerWidth && r.top >= 0 && r.bottom <= innerHeight'));
                    fs.writeFileSync(path.join(__dirname, capture + '-form-' + label + '-submit.png'), await driver.takeScreenshot(), 'base64');
                }
                await driver.sendDevToolsCommand('Emulation.clearDeviceMetricsOverride', {});
            }
            fs.writeFileSync(path.join(__dirname, capture + '-prompt.png'), await driver.takeScreenshot(), 'base64');
            await driver.findElement(By.css(action)).click();
            if (busyRequest) {
                await driver.wait(() => driver.executeScript('return window.libraryBusyProbe.held'), 10000);
                for (const [width, height, label] of [[1440, 900, 'desktop'], [375, 667, 'portrait'], [667, 375, 'landscape']]) {
                    await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: width < 900 });
                    const controls = await driver.findElements(By.css('.library-actions button, .library-search-submit'));
                    assert(controls.length >= 4);
                    assert.equal(await driver.findElement(By.css('.library-status')).getText(), 'Sending library action…');
                    assert.equal(await driver.findElement(By.css('.library-search-submit')).getText(), 'Sending…');
                    for (const control of controls) {
                        assert.equal(await control.isEnabled(), false, `Library action must stay disabled while busy at ${label}`);
                        await driver.executeScript('arguments[0].click()', control);
                    }
                    await driver.executeScript('document.querySelector(".library-search-submit").scrollIntoView({block:"nearest"})');
                    fs.writeFileSync(path.join(__dirname, `library-search-busy-${label}.png`), await driver.takeScreenshot(), 'base64');
                    assert.equal(await driver.executeScript('return window.libraryBusyProbe.commands.length'), 1);
                    assert.deepEqual(await libraryEvents(), beforeEvents);
                    assert.deepEqual(revisions, initialRevisions);
                }
                await driver.sendDevToolsCommand('Emulation.clearDeviceMetricsOverride', {});
                if (rejectBusy) {
                    const beforeConcede = [...revisions];
                    await hubs[0].invoke('Command', gameId, { kind: 'concede' });
                    await driver.wait(() => revisions.every((n, seat) => n > beforeConcede[seat]), 10000);
                    const afterConcede = await libraryEvents();
                    await driver.executeScript('window.libraryBusyProbe.release()');
                    const warning = await driver.wait(until.elementLocated(By.css('.toast-msg')), 10000);
                    const reason = await warning.getText();
                    assert(reason.length > 0, 'Server rejection must be visible');
                    for (const [width, height, label] of [[1440, 900, 'desktop'], [375, 667, 'portrait'], [667, 375, 'landscape']]) {
                        await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: width < 900 });
                        fs.writeFileSync(path.join(__dirname, `library-search-rejected-${label}.png`), await driver.takeScreenshot(), 'base64');
                    }
                    assert.equal(await driver.executeScript('return window.libraryBusyProbe.commands.length'), 1);
                    assert.deepEqual(await libraryEvents(), afterConcede, 'Rejected search cannot append game events');
                    for (let seat = 0; seat < seatCount; seat++) {
                        assert.equal(views[seat].inspectedLibraryCards.length, 0);
                        assert.equal(views[seat].pending, null);
                        await drivers[seat].navigate().refresh();
                        await drivers[seat].wait(until.elementLocated(By.css('.self-table .life')), 20000);
                        await dismissIntro(drivers[seat]);
                        const restored = await fetch(origin + '/api/games/' + gameId, { headers: { Authorization: 'Bearer qa' + seat } }).then(r => r.json());
                        assert.deepEqual(restored.view, views[seat]);
                    }
                    assert.deepEqual(await libraryEvents(), afterConcede);
                    console.log(JSON.stringify({ gameId, seatCount, reason, result: 'Held search rejected after concession; one command, no search events, private views and all-seat reload passed.' }));
                    return;
                }
                await driver.executeScript('window.libraryBusyProbe.release()');
            }
        }
        await driver.wait(() => revisions.every((n, i) => n > initialRevisions[i]), 10000);
        assert.deepEqual(refusals, []);
        if (search) {
            if (lostResponse) {
                await driver.wait(() => driver.executeScript('return window.libraryDeliveryProbe.dropped && window.libraryDeliveryProbe.joined >= 2'), 20000);
                assert.equal(await driver.executeScript('return window.libraryDeliveryProbe.requests.length'), 1);
            }
            assert.equal(views[0].pending.kind, 'search');
            assert(views[0].inspectedLibraryCards.length > 0);
            for (const observer of views.slice(1)) {
                assert.equal(observer.inspectedLibraryCards.length, 0);
                assert.equal(observer.pending.options.length, 0);
            }
            await driver.navigate().refresh();
            await driver.wait(until.elementLocated(By.css(zeroMatch ? '.choice-actions .cancel' : '.choice-option button.choice')), 20000);
            await dismissIntro(driver);
            fs.writeFileSync(path.join(__dirname, capture + '-pending.png'), await driver.takeScreenshot(), 'base64');
            assert(await driver.findElement(By.css('.library-reveal[data-active-inspection]')).getAttribute('open'), 'An authorized search must open its inspection after reload');
            await setInspectionExpanded(driver, false);
            let choices = await driver.findElements(By.css('.choice-option button.choice'));
            if (withdrawalProbe) {
                const events = await libraryEvents();
                for (const [width, height, label] of [[1440, 900, 'desktop'], [667, 375, 'landscape']]) {
                    await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: width < 900 });
                    for (const required of [false, true]) {
                        await driver.executeScript('window.libraryWithdrawalProbe.restore()');
                        await driver.wait(async () => (await driver.findElements(By.css('.choice-option button.choice'))).length === choices.length, 5000);
                        const first = await driver.findElement(By.css('.choice-option button.choice'));
                        await first.sendKeys(Key.ENTER);
                        assert.equal(await first.getAttribute('aria-pressed'), 'true');
                        const revised = await driver.executeScript('return window.libraryWithdrawalProbe.withdraw(arguments[0])', required);
                        assert(revised.remaining > 0);
                        await driver.wait(async () => (await driver.findElements(By.css('.choice-option button.choice'))).length === revised.remaining, 5000);
                        assert.equal((await driver.findElements(By.css('.choice-option button.choice[aria-pressed="true"]'))).length, 0);
                        const confirm = await driver.findElement(By.css('.choice-actions .confirm'));
                        assert.equal(await confirm.isEnabled(), !required, 'Only a required search must disable an empty confirmation');
                        await driver.executeScript('arguments[0].scrollIntoView({block:"center"})', confirm);
                        fs.writeFileSync(path.join(__dirname, `library-search-withdrawal-${required ? 'required' : 'optional'}-${label}.png`), await driver.takeScreenshot(), 'base64');
                        assert.deepEqual(await libraryEvents(), events, 'Simulated view updates must send no game command');
                    }
                }
                await driver.sendDevToolsCommand('Emulation.clearDeviceMetricsOverride', {});
                await driver.navigate().refresh();
                await driver.wait(until.elementLocated(By.css('.choice-option button.choice')), 20000);
                await dismissIntro(driver);
                choices = await driver.findElements(By.css('.choice-option button.choice'));
                console.log(JSON.stringify({ withdrawalProbe: 'Required and optional same-ID view withdrawal rendered correctly; server state unchanged' }));
            }
            if (pointerProbe) {
                assert(choices.length > 0);
                const events = await libraryEvents();
                const observed = [];
                for (const [offset, vertical, hold] of [[20, 0, 0], [-20, 0, 0], [7, 0, 0], [5, 5, 0], [0, 0, 600]]) {
                    await driver.findElement(By.xpath("//button[normalize-space(.)='Start over']")).click();
                    const card = choices[0];
                    const bounds = await card.getRect();
                    assert(bounds.width > 42, 'Both endpoints must remain inside the same option');
                    await driver.actions({ async: true }).move({ origin: card }).press().pause(hold).move({ origin: card, x: offset, y: vertical, duration: 100 }).release().perform();
                    observed.push({ offset, vertical, hold, selected: await card.getAttribute('aria-pressed') });
                    assert.equal(await card.getAttribute('aria-pressed'), 'false', 'A deliberate search drag must not select in either direction');
                    assert.equal(views[0].pending.kind, 'search');
                    assert.deepEqual(await libraryEvents(), events);
                    const gesture = hold ? 'hold' : vertical ? 'diagonal' : offset === 7 ? 'threshold' : offset > 0 ? 'right' : 'left';
                    fs.writeFileSync(path.join(__dirname, `library-search-pointer-${gesture}.png`), await driver.takeScreenshot(), 'base64');
                }
                console.log(JSON.stringify({ gameId, pointerProbe: observed }));
                await driver.findElement(By.xpath("//button[normalize-space(.)='Start over']")).click();
                await choices[0].sendKeys(Key.ENTER);
                assert.equal(await choices[0].getAttribute('aria-pressed'), 'true', 'Keyboard selection remains available');
                await driver.findElement(By.xpath("//button[normalize-space(.)='Start over']")).click();
                await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width: 667, height: 375, deviceScaleFactor: 1, mobile: true });
                await driver.sendDevToolsCommand('Emulation.setTouchEmulationEnabled', { enabled: true, maxTouchPoints: 1 });
                await tapElement(driver, choices[0]);
                assert.equal(await choices[0].getAttribute('aria-pressed'), 'true', 'A phone-landscape tap still selects');
                assert.deepEqual(await libraryEvents(), events);
                assert.equal(views[0].pending.kind, 'search');
                fs.writeFileSync(path.join(__dirname, 'library-search-pointer-touch.png'), await driver.takeScreenshot(), 'base64');
                await driver.sendDevToolsCommand('Emulation.setTouchEmulationEnabled', { enabled: false });
                await driver.sendDevToolsCommand('Emulation.clearDeviceMetricsOverride', {});
                await driver.findElement(By.xpath("//button[normalize-space(.)='Start over']")).click();
            }
            if (zeroPick) {
                assert(choices.length > 0, 'Intentional zero picks must offer matching cards');
                assert(views[0].pending.options.length > 0);
                const pendingEvents = await libraryEvents();
                for (const [width, height, label] of [[1440, 900, 'desktop'], [375, 667, 'portrait'], [667, 375, 'landscape']]) {
                    await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: width < 900 });
                    const confirm = await driver.findElement(By.css('.choice-actions .cancel'));
                    assert.equal(await confirm.isEnabled(), true);
                    await driver.executeScript('arguments[0].scrollIntoView({block:"center"})', confirm);
                    assert(await driver.executeScript('const r=arguments[0].getBoundingClientRect(); return r.left>=0 && r.right<=innerWidth && r.top>=0 && r.bottom<=innerHeight && document.documentElement.scrollWidth<=innerWidth', confirm));
                    fs.writeFileSync(path.join(__dirname, `library-search-zero-pick-${label}.png`), await driver.takeScreenshot(), 'base64');
                    assert.deepEqual(await libraryEvents(), pendingEvents);
                }
                await driver.sendDevToolsCommand('Emulation.clearDeviceMetricsOverride', {});
            }
            if (zeroMatch) {
                assert.equal(choices.length, 0);
                assert.equal(views[0].pending.options.length, 0);
                assert.equal(await driver.findElement(By.css('.choice-actions .cancel')).isEnabled(), true);
                for (const [width, height, label] of [[1440, 900, 'desktop'], [375, 667, 'portrait'], [667, 375, 'landscape']]) {
                    await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: width < 900 });
                    fs.writeFileSync(path.join(__dirname, `library-search-zero-match-${label}.png`), await driver.takeScreenshot(), 'base64');
                }
                await driver.sendDevToolsCommand('Emulation.clearDeviceMetricsOverride', {});
            }
            if (searchBattlefield)
                assert((await Promise.all(choices.map(choice => choice.getText()))).every(label => label === 'Forest'));
            const draftCount = partialSearch ? 1 : Math.max(0, searchCount - 1);
            for (let index = 0; index < draftCount; index++)
                await choices[index].click();
            if (searchPanelProbe) {
                const pendingBefore = structuredClone(views[0].pending);
                const eventsBefore = await libraryEvents();
                const picked = '.choice-option button.choice[aria-pressed="true"]';
                const panel = await driver.findElement(By.css('.library-reveal[data-active-inspection]'));
                const summary = await panel.findElement(By.css('summary'));
                for (const [width, height, label] of [[1440, 900, 'desktop'], [667, 375, 'landscape']]) {
                    await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: width < 900 });
                    if (!(await panel.getAttribute('open')))
                        await summary.click();
                    assert.equal((await panel.findElements(By.css('.is-picked'))).length, draftCount);
                    await summary.click();
                    assert.equal(await panel.getAttribute('open'), null);
                    assert.equal((await driver.findElements(By.css(picked))).length, draftCount);
                    assert.equal(await driver.findElement(By.css('.choice-actions .confirm')).isEnabled(), true);
                    assert.deepEqual(views[0].pending, pendingBefore);
                    assert.deepEqual(await libraryEvents(), eventsBefore, 'Collapse must not submit or cancel the search');
                    fs.writeFileSync(path.join(__dirname, `library-search-collapsed-${label}.png`), await driver.takeScreenshot(), 'base64');
                    await summary.click();
                    assert.equal((await panel.findElements(By.css('.is-picked'))).length, draftCount);
                    assert.deepEqual(views[0].pending, pendingBefore);
                    assert.deepEqual(await libraryEvents(), eventsBefore, 'Reopen must not create a second search');
                    fs.writeFileSync(path.join(__dirname, `library-search-reopened-${label}.png`), await driver.takeScreenshot(), 'base64');
                }
                await summary.click();
                await driver.sendDevToolsCommand('Emulation.clearDeviceMetricsOverride', {});
                console.log(JSON.stringify({ searchPanelProbe: 'Pending choice and selected physical cards survive collapse/reopen without game events' }));
            }
            if (searchBattlefield) {
                assert(choices.length > searchCount, 'Search fixture requires extra matching options');
                const selected = '.choice-option button.choice[aria-pressed="true"]';
                assert.equal((await driver.findElements(By.css(selected))).length, draftCount);
                const pendingEvents = await libraryEvents();
                assert.equal((await driver.findElements(By.css(selected))).length, draftCount);
                assert.match(await driver.findElement(By.css('app-choice-controls [role="status"]')).getText(), /1 remaining/);
                for (const [width, height, label] of [[1440, 900, 'desktop'], [375, 667, 'portrait'], [667, 375, 'landscape']]) {
                    await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: width < 900 });
                    assert.equal(await driver.findElement(By.css('.choice-actions .confirm')).isEnabled(), true);
                    if (label !== 'portrait') {
                        const filter = await driver.findElement(By.css('app-choice-controls app-search-input')).getRect();
                        const options = await driver.findElement(By.css('app-choice-controls .choice-options')).getRect();
                        assert(filter.y + filter.height <= options.y + 1, 'Search header must not overlap the choices');
                    }
                    fs.writeFileSync(path.join(__dirname, `library-search-partial-pick-${label}.png`), await driver.takeScreenshot(), 'base64');
                    assert.deepEqual(await libraryEvents(), pendingEvents);
                }
                await driver.sendDevToolsCommand('Emulation.clearDeviceMetricsOverride', {});
            }
            if (zeroPick || zeroMatch)
                await driver.findElement(By.xpath("//button[normalize-space(.)='Choose none']")).click();
            else if (partialSearch) {
                assert.equal(views[0].pending.kind, 'search', 'A partial selection remains pending until confirmed');
                await driver.findElement(By.css('.choice-actions .confirm')).click();
            }
            else
                await choices[searchCount - 1].click();
            if (choiceBusy) {
                await driver.wait(() => driver.executeScript('return window.libraryBusyProbe.held'), 10000);
                for (const [width, height, label] of [[1440, 900, 'desktop'], [667, 375, 'landscape']]) {
                    await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: width < 900 });
                    const direct = !choiceSkip && !partialSearch;
                    const actions = await driver.findElements(By.css(direct ? '.choice-option button.choice' : '.choice-actions button'));
                    assert.equal(actions.length, direct ? searchCount : 1, 'Only the submitted prompt actions stay visible');
                    assert.equal(await actions[0].isEnabled(), false);
                    for (const action of actions)
                        assert.equal(await action.isEnabled(), false);
                    if (!direct)
                        assert((await actions[0].getText()).includes(choiceSkip ? 'Choose none' : 'Sending'));
                    else
                        assert.equal((await driver.findElements(By.css('.choice-actions button'))).length, 0, 'Direct selection has no second confirmation');
                    await driver.executeScript('arguments[0].scrollIntoView({block:"center"})', actions[0]);
                    fs.writeFileSync(path.join(__dirname, `library-choice-sending-${direct ? 'direct' : choiceSkip ? 'skip' : 'confirm'}-${label}.png`), await driver.takeScreenshot(), 'base64');
                    assert.equal(await driver.executeScript('return window.libraryBusyProbe.commands.length'), 1);
                }
                await driver.sendDevToolsCommand('Emulation.clearDeviceMetricsOverride', {});
                await driver.executeScript('window.libraryBusyProbe.release()');
            }
            await driver.wait(() => views.every(view => !view.pending), 10000);
            assert(views.every(view => view.inspectedLibraryCards.length === 0));
            const afterEvents = await libraryEvents();
            assert.equal(afterEvents.searches - beforeEvents.searches, 1);
            assert.equal(afterEvents.shuffles - beforeEvents.shuffles, searchBattlefield ? 0 : 1);
            assert.equal(afterEvents.reveals - beforeEvents.reveals, searchBattlefield ? 1 : 0);
            if (searchBattlefield) {
                for (const view of views) {
                    const entered = view.battlefield.filter(card => !before.view.battlefield.some(old => old.id.value === card.id.value));
                    assert.equal(entered.length, searchCount);
                    assert(entered.every(card => card.name === 'Forest' && card.tapped));
                    assert.equal(view.lastReveal.cards.length, searchCount);
                    assert(view.lastReveal.cards.every(card => card.name === 'Forest'));
                    assert.equal(view.players[0].handCount, before.view.players[0].handCount);
                }
            }
        }
        if (look) {
            if (lostResponse && !dropStop && !reposition && !arrange) {
                await driver.wait(() => driver.executeScript('return window.libraryDeliveryProbe.dropped && window.libraryDeliveryProbe.joined >= 2'), 20000);
                const probe = await driver.executeScript('return window.libraryDeliveryProbe');
                assert.deepEqual(probe.commands, ['manualLibraryLook']);
            }
            assert.equal(views[0].manualLibraryLookActive, true);
            assert.equal(views[0].inspectedLibraryCards.length, before.view.players[0].libraryCount);
            for (const observer of views.slice(1)) {
                assert.equal(observer.manualLibraryLookActive, false);
                assert.equal(observer.inspectedLibraryCards.length, 0);
            }
            if (waitReady)
                await driver.executeScript('sessionStorage.setItem("qa-library-hold-ready", "1")');
            await driver.navigate().refresh();
            await driver.wait(until.elementLocated(By.css('.library-reveal[data-active-inspection] summary')), 20000);
            if (waitReady) {
                await driver.wait(() => driver.executeScript('return window.libraryReadyProbe.held > 0'), 20000);
                const top = (await libraryOrders())[0][0];
                await checkWaitingInspection(driver, views[0].inspectedLibraryCards, top, 'library-wait-ready-menu');
                await driver.executeScript('window.libraryReadyProbe.release()');
                await driver.wait(() => driver.executeScript('return window.libraryCastReady'), 20000);
                const held = await driver.executeScript('return window.libraryReadyProbe.held');
                await driver.executeScript('window.libraryReadyProbe.reconnect()');
                await driver.wait(() => driver.executeScript('return window.libraryReadyProbe.held > arguments[0]', held), 20000);
                await checkWaitingInspection(driver, views[0].inspectedLibraryCards, top, 'library-reconnect-ready-menu');
                await driver.executeScript('window.libraryReadyProbe.release()');
            }
            if (topPlay)
                await driver.wait(() => driver.executeScript('return window.libraryCastReady'), 20000, 'The refreshed browser must receive its authoritative hub view before acting');
            await dismissIntro(driver);
            await setInspectionExpanded(driver, true);
            if (panelProbe) {
                const panel = await driver.findElement(By.css('.library-reveal[data-active-inspection][open]'));
                const handle = await panel.findElement(By.css('[data-panel-handle]'));
                const initial = await panel.getRect();
                await driver.actions({ async: true }).move({ origin: handle }).press().move({ origin: handle, x: 100, y: 50, duration: 200 }).release().perform();
                const moved = await panel.getRect();
                assert(moved.x > initial.x + 70 && moved.y > initial.y + 30, 'Drag must move the inspection panel');
                await handle.sendKeys(Key.ARROW_LEFT);
                const keyboard = await panel.getRect();
                assert.equal(Math.round(keyboard.x), Math.round(moved.x - 10));
                const x = Math.round(keyboard.x + keyboard.width - 3), y = Math.round(keyboard.y + keyboard.height - 3);
                await driver.actions({ async: true }).move({ origin: 'viewport', x, y }).press().move({ origin: 'viewport', x: x + 60, y: y + 40, duration: 200 }).release().perform();
                await driver.wait(async () => (await panel.getRect()).width > keyboard.width + 30, 5000, 'Native resize must enlarge the panel');
                fs.writeFileSync(path.join(__dirname, 'library-panel-moved-resized-desktop.png'), await driver.takeScreenshot(), 'base64');
                const resized = await panel.getRect();
                const summary = await panel.findElement(By.css('summary'));
                await summary.click();
                await driver.wait(() => driver.executeScript('return !arguments[0].open && !arguments[0].style.width', panel), 5000);
                await summary.click();
                await driver.wait(async () => {
                    const current = await panel.getRect();
                    return Object.keys(resized).every(key => Math.abs(current[key] - resized[key]) <= 1);
                }, 5000, 'Reopening must restore the inspection geometry');
                fs.writeFileSync(path.join(__dirname, 'library-panel-reopened-desktop.png'), await driver.takeScreenshot(), 'base64');
                await driver.actions({ async: true }).move({ origin: handle }).press().move({ origin: 'viewport', x: 1250, y: 740, duration: 200 }).release().perform();
                assert(await driver.executeScript('const r=arguments[0].getBoundingClientRect();return r.left>=0 && r.top>=0 && r.right<=innerWidth && r.bottom<=innerHeight', panel));
                await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width: 667, height: 375, deviceScaleFactor: 1, mobile: true });
                await driver.wait(async () => !(await handle.isDisplayed()), 5000);
                assert(await driver.executeScript('const r=arguments[0].getBoundingClientRect();return r.left>=0 && r.top>=0 && r.right<=innerWidth && r.bottom<=innerHeight && document.documentElement.scrollWidth<=innerWidth', panel));
                fs.writeFileSync(path.join(__dirname, 'library-panel-landscape.png'), await driver.takeScreenshot(), 'base64');
                await driver.sendDevToolsCommand('Emulation.clearDeviceMetricsOverride', {});
                await driver.wait(async () => await handle.isDisplayed(), 5000);
                const beforeReload = await panel.getRect();
                await driver.navigate().refresh();
                await driver.wait(until.elementLocated(By.css('.self-table .life')), 20000);
                await dismissIntro(driver);
                await setInspectionExpanded(driver, true);
                const reloadedPanel = await driver.findElement(By.css('.library-reveal[data-active-inspection][open]'));
                await driver.wait(async () => {
                    const current = await reloadedPanel.getRect();
                    return Object.keys(beforeReload).every(key => Math.abs(current[key] - beforeReload[key]) <= 1);
                }, 5000, 'Page reload must restore the saved panel geometry');
                fs.writeFileSync(path.join(__dirname, 'library-panel-persisted-desktop.png'), await driver.takeScreenshot(), 'base64');
                assert.equal(views.slice(1).every(view => view.inspectedLibraryCards.length === 0), true);
                console.log(JSON.stringify({ panelProbe: 'Drag, keyboard move, native resize, viewport clamp and landscape fallback passed' }));
            }
            if (topPlay) {
                const actionLabel = topLand ? 'Play land' : 'Cast';
                const oracle = topLand ? 'forest' : 'scout';
                const library = (await libraryOrders())[0];
                const card = await inspectedCard(driver, views[0].inspectedLibraryCards, library[0]);
                const oldCount = views[0].players[0].libraryCount;
                const oldCreatures = views[0].battlefield.filter(item => item.oracleId === oracle && item.owner.value === 0).length;
                await card.click();
                await driver.wait(until.elementLocated(By.xpath(`//button[@role='menuitem' and normalize-space(.)='${actionLabel}']`)), 10000);
                for (const [width, height, label] of [[1440, 900, 'desktop'], [667, 375, 'landscape']]) {
                    await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: width < 900 });
                    await driver.wait(() => driver.executeScript(`const rect = document.querySelector('.card-options').getBoundingClientRect();
            return rect.left >= 0 && rect.top >= 0 && rect.right <= innerWidth && rect.bottom <= innerHeight;`), 5000, 'The inspected-card menu must remain inside the resized viewport');
                    fs.writeFileSync(path.join(__dirname, `library-top-${topLand ? 'land' : 'cast'}-menu-${label}.png`), await driver.takeScreenshot(), 'base64');
                }
                await driver.sendDevToolsCommand('Emulation.clearDeviceMetricsOverride', {});
                await driver.findElement(By.xpath(`//button[@role='menuitem' and normalize-space(.)='${actionLabel}']`)).click();
                if (topLand) {
                    await driver.wait(() => views.every(view => view.players[0].libraryCount === oldCount - 1), 10000, 'The played library land must reach every seat');
                    const next = await inspectedCard(driver, views[0].inspectedLibraryCards, (await libraryOrders())[0][0]);
                    await next.click();
                    assert.equal((await driver.findElements(By.xpath("//button[@role='menuitem' and normalize-space(.)='Play land']"))).length, 0, 'The next library land must not be offered after the land drop is spent');
                    await driver.findElement(By.xpath("//button[@role='menuitem' and normalize-space(.)='Close']")).click();
                }
                else {
                    await driver.findElement(By.css('.library-reveal[data-active-inspection] summary')).click();
                    await driver.wait(until.elementLocated(By.css('.cast-next')), 10000);
                    await driver.findElement(By.css('.cast-next')).click();
                    await driver.wait(until.elementLocated(By.css('.cast-submit')), 10000);
                    await driver.executeScript(`window.libraryCastClicks = [];
          document.addEventListener('click', event => window.libraryCastClicks.push({
            text: event.target.textContent, className: event.target.className }), true);`);
                    console.log('CAST_REVIEW', await driver.findElement(By.css('.cast-prompt')).getText());
                    await driver.findElement(By.css('.cast-submit')).click();
                    console.log('CAST_CLICK', JSON.stringify(await driver.executeScript('return window.libraryCastClicks')));
                    await driver.wait(() => views.every(view => view.stack.length === 1 && view.players[0].libraryCount === oldCount - 1), 10000, 'Library cast must reach every seat with one fewer library card');
                    assert(views.every(view => view.stack[0].oracleId === 'scout'));
                    for (let n = 0; views[0].stack.length && n < 4; n++) {
                        const rev = [...revisions];
                        await hubs[views[0].priorityHolder.value].invoke('Command', gameId, { kind: 'pass' });
                        await driver.wait(() => revisions.every((value, seat) => value > rev[seat]), 10000);
                    }
                }
                for (const view of views) {
                    assert.equal(view.stack.length, 0);
                    assert.equal(view.battlefield.filter(item => item.oracleId === oracle && item.owner.value === 0).length, oldCreatures + 1);
                }
                for (let seat = 1; seat < seatCount; seat++)
                    assert.equal(views[seat].inspectedLibraryCards.length, 0);
                await hubs[0].invoke('Command', gameId, { kind: 'manualLibraryStopLooking', requestId: require('node:crypto').randomUUID() });
                await driver.wait(() => views.every(view => view.inspectedLibraryCards.length === 0), 10000);
                for (let seat = 0; seat < seatCount; seat++) {
                    await drivers[seat].navigate().refresh();
                    await drivers[seat].wait(until.elementLocated(By.css('.self-table .life')), 20000);
                    await dismissIntro(drivers[seat]);
                    const restored = await fetch(origin + '/api/games/' + gameId, { headers: { Authorization: 'Bearer qa' + seat } }).then(r => r.json());
                    assert.deepEqual(restored.view, views[seat]);
                }
                fs.writeFileSync(path.join(__dirname, `library-top-${topLand ? 'land' : 'cast'}-resolved.png`), await driver.takeScreenshot(), 'base64');
                assert.deepEqual(refusals, []);
                console.log(JSON.stringify({ gameId, seatCount, topLand, waitReady, result: topLand ? 'Top-library land play, spent land-drop menu revocation, four-seat privacy and reload passed.' : 'Inspection menu delegates top-library cast to shared casting; public stack, resolution, private inspection expiry and all-seat reload passed.' }));
                return;
            }
            if (detailFocus) {
                const counts = [...revisions];
                const card = await driver.findElement(By.css('.library-reveal[data-active-inspection] .inspection-cards button'));
                await driver.executeScript('arguments[0].focus()', card);
                await card.sendKeys(Key.chord(Key.SHIFT, Key.F10));
                const details = await driver.wait(until.elementLocated(By.xpath("//button[@role='menuitem' and contains(., 'detail')]")), 10000);
                await details.sendKeys(Key.ENTER);
                await driver.wait(until.elementLocated(By.css('.card-modal')), 10000);
                if (brokenArtwork) {
                    await driver.wait(until.elementLocated(By.css('.card-modal .modal-art-placeholder')), 10000);
                    assert.equal((await driver.findElements(By.css('.card-modal .modal-card-img'))).length, 0);
                    assert(await driver.executeScript('return performance.getEntriesByType("resource").some(entry => entry.initiatorType === "img" && entry.name.endsWith("/api/qa/unavailable-art") && entry.responseStatus === 404)'), 'The browser must have received an actual artwork 404');
                    assert.equal(await fetch(origin + '/api/qa/unavailable-art').then(r => r.status), 404);
                }
                fs.writeFileSync(path.join(__dirname, 'library-detail-keyboard.png'), await driver.takeScreenshot(), 'base64');
                assert(await driver.executeScript('return document.querySelector(".card-modal").contains(document.activeElement)'), 'Opening card details must move keyboard focus into the detail window');
                await driver.switchTo().activeElement().sendKeys(Key.chord(Key.SHIFT, Key.TAB));
                assert(await driver.executeScript('return document.querySelector(".card-modal").contains(document.activeElement)'), 'Reverse Tab must stay inside details');
                await driver.switchTo().activeElement().sendKeys(Key.TAB);
                assert(await driver.executeScript('return document.querySelector(".card-modal").contains(document.activeElement)'), 'Tab must stay inside details');
                for (const [width, height, label] of [[1440, 900, 'desktop'], [375, 667, 'portrait'], [667, 375, 'landscape']]) {
                    await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: width < 900 });
                    assert(await driver.executeScript('return document.documentElement.scrollWidth <= innerWidth'));
                    assert((await driver.findElement(By.css('.card-modal')).getText()).includes('A sample creature.'));
                    fs.writeFileSync(path.join(__dirname, `library-detail-${brokenArtwork ? 'broken-art' : 'keyboard'}-${label}.png`), await driver.takeScreenshot(), 'base64');
                }
                await driver.sendDevToolsCommand('Emulation.clearDeviceMetricsOverride', {});
                await driver.switchTo().activeElement().sendKeys(Key.ESCAPE);
                await driver.wait(async () => !(await driver.findElements(By.css('.card-modal'))).length, 10000);
                assert(await driver.executeScript('return document.activeElement === arguments[0]', card), 'Closing details must restore focus to the inspected card');
                assert.deepEqual(revisions, counts, 'Inspecting details must not submit a library command');
                console.log(JSON.stringify({ detailFocus, brokenArtwork, result: 'Keyboard detail entry, Tab containment, Escape, focus return and responsive text checks passed.' }));
            }
            if (modeGuard) {
                const counts = [...revisions];
                await driver.findElement(By.css('.library-reveal[data-active-inspection] summary')).click();
                await driver.findElement(By.css('.hand-card-wrapper[aria-label="Play Cinder Spark"]')).click();
                await driver.wait(until.elementLocated(By.css('.cast-prompt')), 10000);
                await driver.findElement(By.css('.library-reveal[data-active-inspection] summary')).click();
                await driver.findElement(By.css('.library-reveal[data-active-inspection] .inspection-cards button')).click();
                await driver.wait(until.elementLocated(By.css('[role="menuitem"]')), 10000);
                assert.equal((await driver.findElements(By.xpath("//button[@role='menuitem' and contains(., 'Put on')]"))).length, 0);
                fs.writeFileSync(path.join(__dirname, 'library-reposition-during-cast.png'), await driver.takeScreenshot(), 'base64');
                for (const [width, height, label] of [[1440, 900, 'desktop'], [375, 667, 'portrait'], [667, 375, 'landscape']]) {
                    await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: width < 900 });
                    assert.equal((await driver.findElements(By.xpath("//button[@role='menuitem' and contains(., 'Put on')]"))).length, 0);
                    assert.deepEqual(revisions, counts);
                    fs.writeFileSync(path.join(__dirname, `library-reposition-during-cast-${label}.png`), await driver.takeScreenshot(), 'base64');
                }
                await driver.sendDevToolsCommand('Emulation.clearDeviceMetricsOverride', {});
                await driver.findElement(By.xpath("//button[@role='menuitem' and contains(., 'Close')]")).click();
                await driver.findElement(By.css('.library-reveal[data-active-inspection] summary')).click();
                await driver.findElement(By.css('.cast-cancel')).click();
                await driver.wait(async () => !(await driver.findElements(By.css('.cast-prompt'))).length, 10000);
                assert.deepEqual(revisions, counts, 'Preparing/cancelling a cast must not mutate the library');
                await driver.findElement(By.css('.library-reveal[data-active-inspection] summary')).click();
            }
            fs.writeFileSync(path.join(__dirname, capture + '-active.png'), await driver.takeScreenshot(), 'base64');
            await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width: 667, height: 375, deviceScaleFactor: 1, mobile: true });
            assert(await driver.executeScript('return document.documentElement.scrollWidth <= innerWidth'));
            fs.writeFileSync(path.join(__dirname, capture + '-landscape.png'), await driver.takeScreenshot(), 'base64');
            await driver.sendDevToolsCommand('Emulation.clearDeviceMetricsOverride', {});
            const prior = [...revisions];
            if (arrange) {
                await driver.findElement(By.css('.library-reveal[data-active-inspection] summary')).click();
                const original = views[0].inspectedLibraryCards.map(card => card.id.value);
                const expected = [...original];
                [expected[0], expected[1]] = [expected[1], expected[0]];
                const revision = revisions[0];
                if (touch) {
                    await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width: 667, height: 375, deviceScaleFactor: 1, mobile: true });
                    await driver.sendDevToolsCommand('Emulation.setTouchEmulationEnabled', { enabled: true, maxTouchPoints: 1 });
                }
                const press = async (selector) => {
                    const element = await driver.wait(until.elementLocated(By.css(selector)), 10000);
                    await driver.wait(until.elementIsVisible(element), 10000);
                    if (touch)
                        await tapElement(driver, element);
                    else
                        await element.click();
                };
                await press('.self-table .zone-pile[data-zone="library"]');
                await press('.library-arrange');
                await press('.library-order-editor .choice-order button:last-child');
                assert.equal(revisions[0], revision, 'Editing must not send a command');
                await press('.library-order-cancel');
                assert.equal(revisions[0], revision, 'Cancel must not send a command');
                await press('.library-arrange');
                if (revokeInspection) {
                    await hubs[0].invoke('Command', gameId, { kind: 'manualLibraryStopLooking', requestId: require('node:crypto').randomUUID() });
                    await driver.wait(() => !views[0].manualLibraryLookActive, 10000);
                    await driver.wait(async () => !(await driver.findElements(By.css('.library-order-editor'))).length, 10000);
                    assert.equal((await driver.findElements(By.css('.library-arrange'))).length, 0);
                    fs.writeFileSync(path.join(__dirname, 'library-arrange-revoked.png'), await driver.takeScreenshot(), 'base64');
                    await press('.library-look');
                    await driver.wait(() => views[0].manualLibraryLookActive, 10000);
                    await press('.self-table .zone-pile[data-zone="library"]');
                    await press('.library-arrange');
                }
                await press('.library-order-editor .choice-order button:last-child');
                assert((await driver.findElement(By.css('.library-order-editor .choice-option button.choice')).getText()).includes(views[0].inspectedLibraryCards[1].name), 'Visible cards follow the draft order');
                for (const [width, height, label] of [[375, 667, 'portrait'], [667, 375, 'landscape']]) {
                    await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: true });
                    await driver.executeScript('document.querySelector(".library-order-editor").scrollIntoView({block:"start"})');
                    assert(await driver.executeScript('return document.documentElement.scrollWidth <= innerWidth'));
                    fs.writeFileSync(path.join(__dirname, capture + '-editor-' + label + '.png'), await driver.takeScreenshot(), 'base64');
                    await driver.executeScript('document.querySelector(".library-order-editor .confirm").scrollIntoView({block:"nearest"})');
                    assert(await driver.executeScript('const r=document.querySelector(".library-order-editor .confirm").getBoundingClientRect(); return r.height >= 44 && r.left >= 0 && r.right <= innerWidth && r.top >= 0 && r.bottom <= innerHeight'));
                }
                await driver.sendDevToolsCommand('Emulation.clearDeviceMetricsOverride', {});
                fs.writeFileSync(path.join(__dirname, capture + '-editor-desktop.png'), await driver.takeScreenshot(), 'base64');
                if (touch)
                    await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width: 667, height: 375, deviceScaleFactor: 1, mobile: true });
                await press('.library-order-editor .confirm');
                if (rejectArrange) {
                    await driver.wait(() => driver.executeScript('return window.libraryBusyProbe.held'), 10000);
                    const orders = await libraryOrders();
                    assert.deepEqual(orders[0], original, 'Held confirmation cannot reorder the library');
                    await hubs[0].invoke('Command', gameId, { kind: 'manualLibraryStopLooking', requestId: require('node:crypto').randomUUID() });
                    await driver.wait(() => views.every(view => view.inspectedLibraryCards.length === 0), 10000);
                    await driver.wait(async () => !(await driver.findElements(By.css('.library-order-editor'))).length, 10000);
                    const counts = await libraryEvents();
                    await driver.executeScript('window.libraryBusyProbe.release()');
                    const warning = await driver.wait(until.elementLocated(By.css('.toast-msg')), 10000);
                    const reason = await warning.getText();
                    assert.equal(reason, 'Library inspection ended. Look again.');
                    for (const [width, height, label] of [[1440, 900, 'desktop'], [375, 667, 'portrait'], [667, 375, 'landscape']]) {
                        await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: width < 900 });
                        assert.equal((await driver.findElements(By.css('.library-order-editor, .library-arrange, .library-reveal[data-active-inspection]'))).length, 0);
                        assert(await driver.executeScript('const el=document.querySelector(".toast-msg"); return el && el.scrollWidth <= el.clientWidth;'), 'Rejection text must fit without truncation');
                        fs.writeFileSync(path.join(__dirname, `library-arrange-rejected-${label}.png`), await driver.takeScreenshot(), 'base64');
                    }
                    assert.equal(await driver.executeScript('return window.libraryBusyProbe.commands.length'), 1);
                    assert.deepEqual(await libraryEvents(), counts);
                    assert.deepEqual(await libraryOrders(), orders, 'Rejected confirmation cannot alter any library order');
                    for (let seat = 0; seat < seatCount; seat++) {
                        await drivers[seat].navigate().refresh();
                        await drivers[seat].wait(until.elementLocated(By.css('.self-table .life')), 20000);
                        await dismissIntro(drivers[seat]);
                        const restored = await fetch(origin + '/api/games/' + gameId, { headers: { Authorization: 'Bearer qa' + seat } }).then(r => r.json());
                        assert.deepEqual(restored.view, views[seat]);
                    }
                    assert.deepEqual(await libraryEvents(), counts);
                    console.log(JSON.stringify({ gameId, seatCount, reason, result: 'Held arrangement rejected after inspection revocation; draft removed, one command, unchanged order/events and all-seat reload passed.' }));
                    return;
                }
                await driver.wait(() => !views[0].manualLibraryLookActive, 10000);
                assert(views.every(view => view.inspectedLibraryCards.length === 0));
                if (lostResponse) {
                    await driver.wait(() => driver.executeScript('return window.libraryDeliveryProbe.dropped && window.libraryDeliveryProbe.joined >= 3'), 20000);
                    await driver.navigate().refresh();
                    await driver.wait(until.elementLocated(By.css('.self-table .life')), 20000);
                    await dismissIntro(driver);
                    const orders = beforeOrders.map(order => [...order]);
                    orders[0] = expected;
                    assert.deepEqual(await libraryOrders(), orders);
                    assert.equal((await libraryEvents()).reorders - beforeEvents.reorders, 1);
                }
                await driver.findElement(By.css('.self-table .zone-pile[data-zone="library"]')).click();
                await driver.findElement(By.css('.library-look')).click();
                await driver.wait(() => views[0].manualLibraryLookActive, 10000);
                assert.deepEqual(views[0].inspectedLibraryCards.map(card => card.id.value), expected, 'The exact confirmed order must be authoritative');
                for (const observer of views.slice(1))
                    assert.equal(observer.inspectedLibraryCards.length, 0);
                if (interveningArrange) {
                    // First arrange A lost its acknowledgement. A confirmed B restores the
                    // original order; selecting A again is now a fresh user intent.
                    if (interveningReposition) {
                        await setInspectionExpanded(driver, true);
                        const card = await inspectedCard(driver, views[0].inspectedLibraryCards, original[0]);
                        await driver.executeScript("arguments[0].scrollIntoView({block:'center'}); arguments[0].dispatchEvent(new MouseEvent('contextmenu', {bubbles:true,cancelable:true}))", card);
                        await driver.findElement(By.xpath("//button[@role='menuitem' and contains(., 'Put on top of library')]")).click();
                    }
                    else {
                        await setInspectionExpanded(driver, false);
                        await press('.self-table .zone-pile[data-zone="library"]');
                        await press('.library-arrange');
                        await press('.library-order-editor .choice-order button:last-child');
                        await press('.library-order-editor .confirm');
                    }
                    await driver.wait(() => !views[0].manualLibraryLookActive, 10000);
                    assert.deepEqual((await libraryOrders())[0], original);
                    await press('.self-table .zone-pile[data-zone="library"]');
                    await press('.library-look');
                    await driver.wait(() => views[0].manualLibraryLookActive, 10000);
                    await press('.self-table .zone-pile[data-zone="library"]');
                    await press('.library-arrange');
                    await press('.library-order-editor .choice-order button:last-child');
                    await press('.library-order-editor .confirm');
                    await driver.wait(() => driver.executeScript('return window.libraryDeliveryProbe.requests.length === 6'), 10000);
                    await driver.wait(() => driver.executeScript('return !Object.keys(sessionStorage).some(key => key.startsWith("mtg.library.request:v1:"))'), 10000);
                    const probe = await driver.executeScript('return window.libraryDeliveryProbe');
                    assert.notEqual(probe.requests[1], probe.requests[5], 'A fresh arrangement after a confirmed different order must not reuse the old uncertain request');
                    assert.deepEqual((await libraryOrders())[0], expected);
                    assert.equal((await libraryEvents()).reorders - beforeEvents.reorders, 3);
                    assert.equal(probe.commands[3], interveningReposition ? 'manualLibraryReposition' : 'manualLibraryArrange');
                    if (interveningReposition)
                        assert.equal(probe.payloads[3].card, original[0]);
                    console.log(JSON.stringify({ interveningArrange: true, interveningReposition, freshRequest: true, reorders: 3 }));
                }
                if (lostResponse && !interveningArrange) {
                    const appliedEvents = await libraryEvents();
                    const appliedOrders = await libraryOrders();
                    await press('.self-table .zone-pile[data-zone="library"]');
                    await press('.library-arrange');
                    await press('.library-order-editor .confirm');
                    await driver.wait(() => driver.executeScript('return window.libraryDeliveryProbe.requests.length === 4'), 10000);
                    await driver.wait(() => driver.executeScript('return !Object.keys(sessionStorage).some(key => key.startsWith("mtg.library.request:v1:"))'), 10000);
                    const probe = await driver.executeScript('return window.libraryDeliveryProbe');
                    assert.deepEqual(probe.commands, ['manualLibraryLook', 'manualLibraryArrange', 'manualLibraryLook', 'manualLibraryArrange']);
                    assert.deepEqual(probe.payloads[3], probe.payloads[1], 'Retry must retain the exact order and request ID');
                    assert.deepEqual(await libraryOrders(), appliedOrders);
                    assert.deepEqual(await libraryEvents(), appliedEvents, 'Retry must not reorder twice');
                    fs.writeFileSync(path.join(__dirname, 'library-arrange-delivery-retry.png'), await driver.takeScreenshot(), 'base64');
                    console.log(JSON.stringify({ deliveryLoss: 'arrange', samePayload: true, reorders: 1, exactLibraryOrdersVerified: true }));
                }
                if (!interveningArrange) {
                    await driver.findElement(By.css('.self-table .zone-pile[data-zone="library"]')).click();
                    await driver.findElement(By.css('.library-look')).click();
                }
                await driver.wait(() => !views[0].manualLibraryLookActive, 10000);
            }
            else if (reposition) {
                const card = await driver.findElement(By.css('.library-reveal[data-active-inspection] .inspection-cards button'));
                if (touch) {
                    await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width: 667, height: 375, deviceScaleFactor: 1, mobile: true });
                    await driver.sendDevToolsCommand('Emulation.setTouchEmulationEnabled', { enabled: true });
                    await tapElement(driver, card);
                }
                else {
                    await driver.executeScript("arguments[0].dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true }))", card);
                }
                const destination = await driver.wait(until.elementLocated(By.xpath(`//button[@role='menuitem' and contains(., 'Put on ${placement} of library')]`)), 10000);
                fs.writeFileSync(path.join(__dirname, `library-reposition-${placement}${touch ? "-touch" : ""}-menu-desktop.png`), await driver.takeScreenshot(), 'base64');
                await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width: 667, height: 375, deviceScaleFactor: 1, mobile: true });
                assert(await driver.executeScript('return document.documentElement.scrollWidth <= innerWidth'));
                fs.writeFileSync(path.join(__dirname, `library-reposition-${placement}${touch ? "-touch" : ""}-menu-landscape.png`), await driver.takeScreenshot(), 'base64');
                if (touch)
                    await tapElement(driver, destination);
                else
                    await destination.click();
                await driver.sendDevToolsCommand('Emulation.clearDeviceMetricsOverride', {});
            }
            else {
                await driver.findElement(By.css('.library-reveal[data-active-inspection] summary')).click();
                await driver.findElement(By.css('.self-table .zone-pile[data-zone="library"]')).click();
                for (const [width, height, label] of [[1440, 900, 'desktop'], [375, 667, 'portrait'], [667, 375, 'landscape']]) {
                    await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: width < 900 });
                    const stopControl = await driver.findElement(By.css('.library-look'));
                    assert.match(await stopControl.getText(), /stop looking/i);
                    assert(await stopControl.isEnabled());
                    await driver.executeScript('arguments[0].scrollIntoView({block:"center"})', stopControl);
                    fs.writeFileSync(path.join(__dirname, `library-look-stop-control-${label}.png`), await driver.takeScreenshot(), 'base64');
                }
                await driver.sendDevToolsCommand('Emulation.clearDeviceMetricsOverride', {});
                const stop = await driver.wait(until.elementLocated(By.css('.library-look')), 10000);
                assert.equal(await stop.getText(), 'Stop looking');
                await stop.click();
            }
            await driver.wait(() => revisions.every((value, index) => value > prior[index]), 10000);
            await driver.wait(async () => !(await driver.findElements(By.css('.library-reveal[data-active-inspection]'))).length, 10000);
            for (const view of views) {
                assert.equal(view.manualLibraryLookActive, false);
                assert.equal(view.inspectedLibraryCards.length, 0);
            }
        }
        if (replacement) {
            assert(views[0].pending, 'The library operation opens a real replacement choice');
            assert.equal(views[0].stack.length, 0, 'No completed activation is on the stack while the choice is pending');
            if (lostResponse) {
                await driver.wait(() => driver.executeScript('return window.libraryDeliveryProbe.dropped && window.libraryDeliveryProbe.joined >= 2'), 20000);
                await driver.wait(until.elementLocated(By.css('dialog .library-status')), 10000);
                const warnings = await driver.findElements(By.css('.toast-msg')).then(items => Promise.all(items.map(item => item.getText())));
                assert.equal(warnings.filter(text => text.includes('Library action was not confirmed')).length, 0);
                assert(!warnings.some(text => text.includes('The action could not be sent')), 'Library UI owns the uncertainty warning');
                assert((await driver.findElement(By.css('dialog .library-status')).getText()).includes('Library action was not confirmed'));
                fs.writeFileSync(path.join(__dirname, 'library-lost-response-warning.png'), await driver.takeScreenshot(), 'base64');
                for (const [width, height, label] of [[375, 667, 'portrait'], [667, 375, 'landscape']]) {
                    await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: true });
                    assert(await driver.executeScript('const r=document.querySelector("dialog .library-status").getBoundingClientRect(); return document.documentElement.scrollWidth <= innerWidth && r.left >= 0 && r.right <= innerWidth && r.top >= 0 && r.bottom <= innerHeight'));
                    fs.writeFileSync(path.join(__dirname, 'library-lost-response-warning-' + label + '.png'), await driver.takeScreenshot(), 'base64');
                }
                await driver.sendDevToolsCommand('Emulation.clearDeviceMetricsOverride', {});
                const close = await driver.findElements(By.css('.viewer-close'));
                if (close.length)
                    await close[0].click();
            }
            else
                await driver.navigate().refresh();
            await driver.wait(until.elementLocated(By.css('.choice-option button.choice')), 20000);
            await dismissIntro(driver);
            fs.writeFileSync(path.join(__dirname, capture + '-pending.png'), await driver.takeScreenshot(), 'base64');
            const optionLabels = await driver.findElements(By.css('.choice-option button.choice')).then(items => Promise.all(items.map(item => item.getText())));
            assert(optionLabels.every(label => !label.includes('WouldBePutIntoZone')));
            assert(optionLabels.every(label => label.includes(draw ? 'Replace a card draw' : shuffle ? 'Replace shuffling the library' : 'Replace a move to the graveyard')));
            await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width: 667, height: 375, deviceScaleFactor: 1, mobile: true });
            assert(await driver.executeScript('return document.documentElement.scrollWidth <= innerWidth'));
            fs.writeFileSync(path.join(__dirname, capture + '-pending-landscape.png'), await driver.takeScreenshot(), 'base64');
            await driver.sendDevToolsCommand('Emulation.setDeviceMetricsOverride', { width: 375, height: 667, deviceScaleFactor: 1, mobile: true });
            fs.writeFileSync(path.join(__dirname, capture + '-pending-portrait.png'), await driver.takeScreenshot(), 'base64');
            await driver.sendDevToolsCommand('Emulation.clearDeviceMetricsOverride', {});
            let answers = 0;
            while (views[0].pending) {
                assert(++answers <= seatCount * 4, 'Bound replacement decisions for two library cards and two sources per seat');
                for (const observer of views.slice(1)) {
                    assert.equal(observer.pending.context.remainingPayment ?? null, null);
                    assert.equal(observer.pending.context.event ?? null, null);
                }
                const revision = revisions[0];
                await driver.wait(until.elementLocated(By.css('.choice-option button.choice')), 10000);
                await driver.findElement(By.css('.choice-option button.choice')).click();
                await driver.wait(() => revisions[0] > revision, 10000);
            }
            await driver.wait(() => views.slice(1).every(view => !view.pending), 10000);
        }
        if (lostResponse && reposition) {
            await driver.wait(() => driver.executeScript('return window.libraryDeliveryProbe.dropped && window.libraryDeliveryProbe.joined >= 3'), 20000);
            const expected = beforeOrders.map(order => [...order]);
            const firstProbe = await driver.executeScript('return window.libraryDeliveryProbe');
            const moved = firstProbe.payloads[1].card;
            const movedIndex = expected[0].indexOf(moved);
            assert(movedIndex >= 0, 'The selected card must belong to the original library');
            expected[0].splice(movedIndex, 1);
            if (placement === 'bottom')
                expected[0].push(moved);
            else
                expected[0].unshift(moved);
            assert.deepEqual(await libraryOrders(), expected, 'Reposition must preserve every other card and library order');
            const appliedEvents = await libraryEvents();
            assert.equal(appliedEvents.reorders - beforeEvents.reorders, 1);
            await driver.navigate().refresh();
            await driver.wait(until.elementLocated(By.css('.self-table .life')), 20000);
            await dismissIntro(driver);
            assert.equal((await driver.findElements(By.css('.library-reveal[data-active-inspection]'))).length, 0);
            await driver.findElement(By.css('.self-table .zone-pile[data-zone="library"]')).click();
            await driver.findElement(By.css('.library-look')).click();
            await driver.wait(() => views[0].manualLibraryLookActive, 10000);
            await setInspectionExpanded(driver, true);
            const card = await inspectedCard(driver, views[0].inspectedLibraryCards, moved);
            await driver.executeScript("arguments[0].scrollIntoView({block:'center'}); arguments[0].dispatchEvent(new MouseEvent('contextmenu', {bubbles:true,cancelable:true}))", card);
            await driver.findElement(By.xpath(`//button[@role='menuitem' and contains(., 'Put on ${placement} of library')]`)).click();
            await driver.wait(() => driver.executeScript('return window.libraryDeliveryProbe.requests.length === 4'), 10000);
            await driver.wait(() => driver.executeScript('return !Object.keys(sessionStorage).some(key => key.startsWith("mtg.library.request:v1:"))'), 10000);
            const probe = await driver.executeScript('return window.libraryDeliveryProbe');
            assert.deepEqual(probe.commands, ['manualLibraryLook', 'manualLibraryReposition', 'manualLibraryLook', 'manualLibraryReposition']);
            assert.equal(probe.requests[1], probe.requests[3], 'Reposition retry must reuse its accepted request ID');
            assert.deepEqual(probe.payloads[3], probe.payloads[1], 'Retry must select the identical card and destination');
            assert.deepEqual(await libraryOrders(), expected);
            assert.deepEqual(await libraryEvents(), appliedEvents, 'Retry must not emit a second reorder');
            console.log(JSON.stringify({ deliveryLoss: 'reposition', placement, moved,
                requestIdReused: probe.requests[1] === probe.requests[3],
                exactLibraryOrdersVerified: true, reorders: appliedEvents.reorders - beforeEvents.reorders }));
            await driver.findElement(By.css('.library-reveal[data-active-inspection] summary')).click();
            await driver.findElement(By.css('.self-table .zone-pile[data-zone="library"]')).click();
            await driver.findElement(By.css('.library-look')).click();
            await driver.wait(() => !views[0].manualLibraryLookActive, 10000);
            fs.writeFileSync(path.join(__dirname, 'library-reposition-' + placement + '-retry.png'), await driver.takeScreenshot(), 'base64');
        }
        if (lostResponse && look && !reposition && !arrange) {
            if (dropStop) {
                await driver.wait(() => driver.executeScript('return window.libraryDeliveryProbe.dropped && window.libraryDeliveryProbe.joined >= 3'), 20000);
                await driver.navigate().refresh();
                await driver.wait(until.elementLocated(By.css('.self-table .life')), 20000);
                await dismissIntro(driver);
                assert.equal((await driver.findElements(By.css('.library-reveal[data-active-inspection]'))).length, 0);
                await driver.findElement(By.css('.self-table .zone-pile[data-zone="library"]')).click();
                assert.equal(await driver.findElement(By.css('.library-look')).getText(), 'Look at library');
                await driver.findElement(By.css('.viewer-close')).click();
            }
            const probe = await driver.executeScript('return window.libraryDeliveryProbe');
            assert.deepEqual(probe.commands, ['manualLibraryLook', 'manualLibraryStopLooking']);
            assert.notEqual(probe.requests[0], probe.requests[1], 'Stop looking is a new action, not a retry of look');
            // A later inspection cycle must not inherit the uncertain request from the old one.
            await driver.findElement(By.css('.self-table .zone-pile[data-zone="library"]')).click();
            await driver.findElement(By.css('.library-look')).click();
            await driver.wait(() => views[0].manualLibraryLookActive, 10000);
            await setInspectionExpanded(driver, false);
            await driver.findElement(By.css('.self-table .zone-pile[data-zone="library"]')).click();
            await driver.findElement(By.css('.library-look')).click();
            await driver.wait(() => views.every(view => !view.manualLibraryLookActive), 10000);
            const repeated = await driver.executeScript('return window.libraryDeliveryProbe');
            assert.deepEqual(repeated.commands, ['manualLibraryLook', 'manualLibraryStopLooking', 'manualLibraryLook', 'manualLibraryStopLooking']);
            assert.equal(new Set(repeated.requests).size, 4, 'Each completed inspection cycle needs fresh action IDs');
            assert.deepEqual(await libraryEvents(), beforeEvents, 'Inspection must not search, shuffle, reveal or mill');
            assert.deepEqual(await libraryOrders(), beforeOrders, 'Inspection must preserve the exact order of every library');
            fs.writeFileSync(path.join(__dirname, capture + (dropStop ? '-stop' : '') + '-delivery-recovered.png'), await driver.takeScreenshot(), 'base64');
            console.log(JSON.stringify({ deliveryLoss: dropStop ? 'stopLooking' : 'look', commands: probe.commands, exactLibraryOrderPreserved: true }));
        }
        if (lostResponse && !look) {
            const completed = (await snapshot()).view;
            const completedEvents = await libraryEvents();
            if (shuffle)
                assert.equal(completedEvents.shuffles - beforeEvents.shuffles, 1, 'The first request must complete exactly one shuffle');
            if (reloadResponse) {
                await driver.navigate().refresh();
                await driver.wait(until.elementLocated(By.css('.self-table .life')), 20000);
                await dismissIntro(driver);
            }
            await driver.findElement(By.css('.self-table .zone-pile[data-zone="library"]')).click();
            const retryAction = search ? '.library-search-submit' : draw ? '.library-draw' : '.library-shuffle';
            await driver.wait(until.elementLocated(By.css(retryAction)), 10000);
            if (search) {
                await configureSearch(driver);
            }
            await driver.findElement(By.css(retryAction)).click();
            await driver.wait(() => driver.executeScript('return window.libraryDeliveryProbe.requests.length === 2'), 10000);
            await driver.wait(async () => !(await driver.findElements(By.css('.viewer-close'))).length, 10000);
            const probe = await driver.executeScript('return window.libraryDeliveryProbe');
            assert.equal(probe.requests[0], probe.requests[1], 'Retry must reuse the accepted request ID');
            assert.deepEqual((await snapshot()).view, completed, 'Retry must not repeat the library operation');
            assert.deepEqual(await libraryEvents(), completedEvents, 'Retry must not append another library event');
            fs.writeFileSync(path.join(__dirname, capture + '-retry.png'), await driver.takeScreenshot(), 'base64');
        }
        for (const view of views) {
            assert.equal(view.players[0].life, before.view.players[0].life - (0));
            assert.equal(view.players[0].libraryCount, before.view.players[0].libraryCount - (search ? searchCount : draw ? 1 : shuffle || look ? 0 : replacement ? 2 : 3));
            assert.equal(view.stack.length, 0);
        }
        if (zeroMatch || zeroPick)
            assert.deepEqual(views[0].hand, before.view.hand);
        if (draw || search && !searchBattlefield && !zeroMatch && !zeroPick) {
            const receivedCount = search ? searchCount : 1;
            assert.equal(views[0].hand.length, before.view.hand.length + receivedCount);
            const drawn = views[0].hand.filter(card => !before.view.hand.some(old => old.id.value === card.id.value));
            assert.equal(drawn.length, receivedCount);
            assert(drawn.every(card => card.oracleId));
            if (search)
                assert(drawn.every(card => card.types.includes('Land')), 'The configured land filter must constrain every chosen card');
            const privateIds = new Set(drawn.map(card => card.id.value));
            const checkPrivateCard = value => {
                if (!value || typeof value !== 'object')
                    return;
                assert(!(value.oracleId && privateIds.has(value.id?.value)), 'Opponent projection must not include any drawn card identity');
                for (const child of Object.values(value))
                    checkPrivateCard(child);
            };
            for (const observer of views.slice(1)) {
                checkPrivateCard(observer);
                assert.equal(observer.players[0].handCount, before.view.players[0].handCount + receivedCount);
            }
        }
        for (let seat = 0; seat < seatCount; seat++) {
            await drivers[seat].wait(async () => {
                if (seat > 0 && seatCount === 4) {
                    const plates = await drivers[seat].findElements(By.css('.opponent-plate'));
                    for (const plate of plates) {
                        if ((await plate.findElement(By.css('strong')).getText()) !== 'Alex')
                            continue;
                        assert.equal(await plate.findElement(By.css('.plate-main b')).getText(), String(before.view.players[0].life - (0)));
                        if (draw)
                            assert((await plate.findElement(By.css('.plate-meta')).getText()).includes('Hand ' + (before.view.players[0].handCount + 1)));
                        return true;
                    }
                    return false;
                }
                return (await drivers[seat].findElement(By.css(seat === 0 ? '.self-table .life' : '.opponent-boards .life')).getText()).includes(String(before.view.players[0].life - (0)));
            }, 10000);
            fs.writeFileSync(path.join(__dirname, capture + '-seat-' + seat + '.png'), await drivers[seat].takeScreenshot(), 'base64');
        }
        const finalEvents = await libraryEvents();
        for (let seat = 0; seat < seatCount; seat++) {
            const expected = views[seat];
            await drivers[seat].navigate().refresh();
            await drivers[seat].wait(until.elementLocated(By.css('.self-table .life')), 20000);
            await dismissIntro(drivers[seat]);
            const reloaded = await fetch(origin + '/api/games/' + gameId, { headers: { Authorization: 'Bearer qa' + seat } }).then(r => r.json());
            assert.deepEqual(reloaded.view, expected, 'Reload retains the completed library operation for seat ' + seat);
        }
        assert.deepEqual(await libraryEvents(), finalEvents, 'All-seat reload must not append library events');
        console.log(JSON.stringify({ gameId, seatCount, replacement, shuffle, draw, search, searchBattlefield, look, reposition, arrange, placement, touch, revokeInspection, revisions, result: arrange ? 'Local arrangement edits, cancel, exact-order confirmation, private reinspection and four-seat reload passed.' : searchBattlefield ? 'Basic Forest search, public reveal, tapped entry, no shuffle, pending reload and all-seat broadcast passed.' : search ? 'Filtered manual search, private inspection, pending reload, automatic capacity or explicit partial submission, hand privacy and all-seat broadcast passed.' : reposition ? 'Library reposition menu, inspection expiry, all-seat broadcast and final reload passed.' : look ? 'Private library look, active reload, stop, expiry, all-seat broadcast and final reload passed.' : 'Live browser seats: manual library action, pending refresh, replacement decisions, broadcast and reload passed.' }));
    }
    catch (error) {
        if (drivers.length) {
            if (topPlay)
                console.error(JSON.stringify({ commands: await drivers[0].executeScript('return window.libraryCastProbe'), revisions, refusals,
                    browser: await drivers[0].manage().logs().get('browser'),
                    views: views.map(view => ({ stack: view.stack.length, libraryCount: view.players[0].libraryCount })) }));
            if (lostResponse)
                console.error(JSON.stringify(await drivers[0].executeScript('return window.libraryDeliveryProbe')));
            fs.writeFileSync(path.join(__dirname, 'library-live-failure.html'), await drivers[0].getPageSource());
            fs.writeFileSync(path.join(__dirname, 'library-live-failure.png'), await drivers[0].takeScreenshot(), 'base64');
        }
        throw error;
    }
    finally {
        for (const driver of drivers)
            await driver.quit();
        for (const hub of hubs)
            await hub.stop();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });

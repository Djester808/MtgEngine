const path = require('node:path');
const { By } = require(path.resolve(__dirname, '../../../mtg-client/e2e/node_modules/selenium-webdriver'));

// Express the intended presentation state instead of assuming the prior default.
async function setInspectionExpanded(driver, expanded) {
  const panel = await driver.findElement(By.css('.library-reveal[data-active-inspection]'));
  if (Boolean(await panel.getAttribute('open')) !== expanded)
    await panel.findElement(By.css('summary')).click();
}
module.exports = { setInspectionExpanded };

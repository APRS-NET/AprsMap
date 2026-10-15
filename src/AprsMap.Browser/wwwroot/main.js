// SPDX-License-Identifier: AGPL-3.0-only
import { dotnet } from './_framework/dotnet.js';

// The loading page is replaced by the app once it starts. When it can't (no gateway at this address, a build that
// can't run), the page says why instead of leaving "Loading…" up forever (docs/status.md).
const out = document.getElementById('out');
let started = false;

// Only if the .NET runtime itself never gets going. The app's own messages are translated; these can't be, because
// they show before any app code runs.
const slow = setTimeout(() => {
  if (!started) {
    const hint = document.createElement('p');
    hint.className = 'startup-hint';
    hint.textContent =
      'Still loading. If this doesn’t change, the gateway may not be running: open the app from the gateway’s address.';
    out.querySelector('.loading')?.append(hint);
  }
}, 45000);

function showError(title, detail, retry) {
  started = true;
  clearTimeout(slow);
  const box = document.createElement('div');
  box.className = 'startup-error';
  box.setAttribute('role', 'alert');
  const heading = document.createElement('h1');
  heading.textContent = title;
  const text = document.createElement('p');
  text.textContent = detail;
  const button = document.createElement('button');
  button.textContent = retry;
  button.addEventListener('click', () => globalThis.location.reload());
  box.append(heading, text, button);
  out.replaceChildren(box);
  button.focus();
}

try {
  const runtime = await dotnet.withDiagnosticTracing(false).create();
  runtime.setModuleImports('main.js', {
    showStartupError: showError,
    appStarted: () => {
      started = true;
      clearTimeout(slow);
    },
  });
  const config = runtime.getConfig();
  // The page URL tells the app which gateway served it.
  await runtime.runMain(config.mainAssemblyName, [globalThis.location.href]);
} catch (error) {
  // Usually a development build of the browser project run on its own: without a publish, the runtime can't load.
  showError(
    'The web app couldn’t start',
    'The web app runs from its gateway. A development build of the browser project can’t run on its own.\n' +
      'Start the gateway (for example with docker compose up), then open the app from the gateway’s address, ' +
      'such as http://localhost:8080.\n\nDetails: ' +
      String(error),
    'Try again',
  );
}

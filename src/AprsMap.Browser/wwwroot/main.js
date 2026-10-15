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

// The browser's location for the setup wizard ("This device's location"). Resolves with "lat,lon,accuracy"; rejects
// with "geo:denied", "geo:unavailable", "geo:timeout", "geo:insecure" or "geo:unsupported". The browser asks the user.
function getPosition() {
  return new Promise((resolve, reject) => {
    if (!globalThis.isSecureContext) {
      reject(new Error('geo:insecure'));
    } else if (!navigator.geolocation) {
      reject(new Error('geo:unsupported'));
    } else {
      navigator.geolocation.getCurrentPosition(
        (p) => resolve(`${p.coords.latitude},${p.coords.longitude},${p.coords.accuracy ?? ''}`),
        (e) => reject(new Error(['geo:unavailable', 'geo:denied', 'geo:unavailable', 'geo:timeout'][e.code] ?? 'geo:unavailable')),
        { enableHighAccuracy: false, timeout: 20000, maximumAge: 60000 },
      );
    }
  });
}

// The viewer's own preferences (F0.6). Storage can be refused (private browsing, blocked site data): then nothing is
// kept, and the app runs with its defaults.
function prefGet(key) {
  try {
    return globalThis.localStorage.getItem(key);
  } catch {
    return null;
  }
}

function prefSet(key, value) {
  try {
    globalThis.localStorage.setItem(key, value);
  } catch {
    // Nowhere to keep it.
  }
}

function prefRemove(key) {
  try {
    globalThis.localStorage.removeItem(key);
  } catch {
    // Nothing to remove.
  }
}

function prefClear(prefix) {
  try {
    const storage = globalThis.localStorage;
    for (const key of Object.keys(storage)) {
      if (key.startsWith(prefix)) {
        storage.removeItem(key);
      }
    }
  } catch {
    // Nothing to clear.
  }
}

const prefersReducedMotion = () => globalThis.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;
const coarsePointer = () => globalThis.matchMedia?.('(pointer: coarse)').matches ?? false;

try {
  const runtime = await dotnet.withDiagnosticTracing(false).create();
  runtime.setModuleImports('main.js', {
    showStartupError: showError,
    getPosition,
    prefGet,
    prefSet,
    prefRemove,
    prefClear,
    reloadPage: () => globalThis.location.reload(),
    screenPixels: () =>
      (globalThis.screen?.width ?? 1280) * (globalThis.screen?.height ?? 800) * (globalThis.devicePixelRatio ?? 1) ** 2,
    prefersReducedMotion,
    coarsePointer,
    appStarted: () => {
      started = true;
      clearTimeout(slow);
      // A marker for the browser smoke test (tools/ci/browser-smoke.py): the app is running.
      document.documentElement.dataset.appStarted = 'true';
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

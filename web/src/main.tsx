import { createRoot } from 'react-dom/client';
import { App } from './app';
import { claimLaunchTicket } from './lib/api';
import { applyTheme } from './lib/theme';

applyTheme();

// Claim the launch ticket before the first /api call, so the page never treats the URL as the session token.
void claimLaunchTicket().then(() => {
  const root = document.getElementById('root');
  if (root) createRoot(root).render(<App />);
});

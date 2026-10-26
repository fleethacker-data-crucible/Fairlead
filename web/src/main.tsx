import { createRoot } from 'react-dom/client';
import { App } from './app';
import { captureSessionToken } from './lib/api';
import { applyTheme } from './lib/theme';

applyTheme();
captureSessionToken();

const root = document.getElementById('root');
if (root) createRoot(root).render(<App />);

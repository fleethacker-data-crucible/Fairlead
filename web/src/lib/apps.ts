// The Fleet tools that get their Fairlead key straight from Fairlead ("Install for an app"), and the rule for any other
// app name. The server enforces the same rule; this keeps the form from sending a name it will refuse.
export const FLEET_APPS = ['PacketRat', 'Bowerbird', 'Gangplank', 'Halyard', 'Semaphore', 'SnapShackle', 'BridgeWarden', 'Bosun', 'TalkForge'] as const;

const APP_NAME = /^[A-Za-z][A-Za-z0-9-]{0,39}$/;

export function isValidAppName(name: string): boolean {
  return APP_NAME.test(name);
}

/** Where an installed key lives: the generic credential the app reads from Windows Credential Manager. */
export function credentialTarget(app: string): string {
  return `Fairlead/${app}`;
}

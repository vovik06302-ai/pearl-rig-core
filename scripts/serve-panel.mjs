import { createPanelServer } from './panel-server.mjs';
createPanelServer().listen(3000, '127.0.0.1', () => console.log('Pearl panel: http://127.0.0.1:3000'));

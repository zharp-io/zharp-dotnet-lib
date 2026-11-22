// Every scenario, by name: node e2e/run.mjs <name…>
import { web } from './web.mjs';
import { consoleApp, httpLogs } from './console.mjs';

export const SCENARIOS = [
  web({ tfm: 'net10.0', db: 'pg' }),
  { ...web({ tfm: 'net10.0', db: 'mssql' }), mssql: true },
  web({ tfm: 'net8.0', db: 'pg' }),
  web({ tfm: 'net11.0', db: 'pg' }),
  consoleApp,
  httpLogs,
];

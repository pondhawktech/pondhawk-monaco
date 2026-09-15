// Stand-in for monaco-yaml. configureMonacoYaml returns a disposable that code-editor.js replaces on
// every schema change, so the tests need to see both the call and the disposal of the previous one.
import { log } from './fake-monaco.js';

export function configureMonacoYaml(_monaco, options) {
  log.push({ name: 'configureMonacoYaml', args: [options, _monaco] });
  return { dispose: () => log.push({ name: 'yaml.dispose', args: [] }) };
}

// Native Worker Threads clear execArgv, so their exact dependencies need a
// checked untracked junction on Windows or directory link on POSIX.
import { bindTools } from './candidate-support.mjs';
bindTools(process.argv[2]);

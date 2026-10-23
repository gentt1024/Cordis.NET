"""Summarize only the real GitHub prechecks, without dumping the environment."""
import argparse
from datetime import datetime
import json
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('directory', type=Path)
args = parser.parse_args()
result = []
for trace in sorted(args.directory.glob('*/git-trace.jsonl')):
    events = [json.loads(line) for line in trace.read_text(encoding='utf-8').splitlines()]
    starts = [event for event in events if event['event'] == 'start'
              and event.get('argv', [])[1:4] == ['-c', 'credential.helper=', 'ls-remote']]
    for start in starts:
        sid = start['sid']
        own = [event for event in events if event['sid'] == sid]
        exits = [event for event in own if event['event'] == 'exit']
        terminal = exits[-1] if exits else None
        result.append({
            'process': trace.parent.name,
            'repository': start['argv'][-2],
            'start': start['time'],
            'exitCode': terminal.get('code') if terminal else None,
            'gitElapsedSeconds': terminal.get('t_abs') if terminal else None,
            'wallElapsedSeconds': (datetime.fromisoformat(terminal['time'].replace('Z', '+00:00'))
                                   - datetime.fromisoformat(start['time'].replace('Z', '+00:00'))).total_seconds() if terminal else None,
            'lastEvent': own[-1],
            'children': [event for event in own if event['event'] in ('child_start', 'child_exit')],
            'errors': [event for event in events if (event['sid'] == sid or event['sid'].startswith(sid + '/'))
                       and event['event'] == 'error'],
        })
output = args.directory / 'git-precheck-summary.json'
output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
print(json.dumps(result, indent=2))

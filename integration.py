import json, pathlib, subprocess, threading, queue, sys, time, os

root = pathlib.Path(__file__).resolve().parent
folder = pathlib.Path(os.environ.get('RIMTOGETHER_INTEGRATION_DIR', str(root / 'work-integration')))
folder.mkdir(exist_ok=True, parents=True)
server = pathlib.Path(sys.argv[2]).resolve() if len(sys.argv) > 2 else root / 'Server/bin/Release/net9.0/RimTogether.Server.exe'
replay = pathlib.Path(sys.argv[1]).resolve()
config = folder / 'config.json'
config.write_text(json.dumps({'Username':'Host','HostAddress':sys.argv[3] if len(sys.argv) > 3 else '127.0.0.1','Password':'test-only-pass','SeedReplay':str(replay)}), encoding='utf-8')

def session(expected=None):
    lines = queue.Queue()
    process = subprocess.Popen([str(server), str(config)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding='utf-8', errors='replace')
    captured = []
    def reader():
        for line in process.stdout:
            captured.append(line)
            lines.put(line)
    threading.Thread(target=reader, daemon=True).start()
    try:
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            line = lines.get(timeout=3)
            if line.startswith('READY '):
                if expected is not None and f'tick={expected}' not in line: raise AssertionError(line)
                backup = folder / 'world/checkpoint.json.bak'
                if backup.exists(): json.loads(backup.read_text(encoding='utf-8'))
                break
        else: raise AssertionError('No READY')
        process.stdin.write('save\nstop\n'); process.stdin.flush()
        result = process.wait(timeout=15)
        if result: raise AssertionError('Server failed: ' + ''.join(captured))
        assert any(line.startswith('CHECKPOINT OK ') and 'background-ms=' in line for line in captured), ''.join(captured)
        assert any(line.startswith('STOPPED') for line in captured), ''.join(captured)
        print('PASS server start/save/stop; ' + next(x.strip() for x in captured if x.startswith('READY')))
    finally:
        if process.poll() is None: process.kill(); process.wait()
    return captured

session()
state = folder / 'world/checkpoint.json'
snapshot = json.loads(state.read_text(encoding='utf-8'))
snapshot['Tick'] += 17
state.write_text(json.dumps(snapshot), encoding='utf-8')
session(snapshot['Tick'])
assert pathlib.Path(str(state) + '.bak').exists()
print('PASS persisted tick restored after independent process restart')
# Corrupt only the primary: backup recovery must preserve the world.
state.write_text('{broken', encoding='utf-8')
logs = session()
assert any('CHECKPOINT WARNING' in line for line in logs)
print('PASS corrupted primary checkpoint recovers from backup')
print('ALL PROCESS CHECKS PASSED')

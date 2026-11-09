"""Two real RimWorld clients against an installed production server. No player save writes.

Requires an already isolated .build/unity-game tree and a built SoakHarness DLL.
Only .build/soak-* data/config directories are used. The extra fault-injection mod
is deployed exclusively inside .build/unity-game and is not part of release bundles.
"""
import argparse, datetime as dt, hashlib, json, os, pathlib, shutil, subprocess
import threading, time, xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument('--before-seconds', type=int, default=600)
parser.add_argument('--after-seconds', type=int, default=600)
parser.add_argument('--bundle', type=pathlib.Path, help='Explicit locally built candidate bundle; default is installed production release')
parser.add_argument('--batchmode', action='store_true', help='Diagnostic only; default uses normal windowed Unity rendering')
args = parser.parse_args()
repo = pathlib.Path(__file__).resolve().parents[1]
build = repo/'.build'
game_root = (build/'unity-game').resolve()
assert game_root.is_relative_to(build.resolve()), 'Test game must stay inside workspace .build'
app_root = pathlib.Path(os.environ['LOCALAPPDATA'])/'RimTogether/app'
installed = json.loads((app_root/'current.json').read_text(encoding='utf-8-sig'))
manifest = installed['Manifest']
bundle = app_root/'versions'/(manifest['Tag']+'-'+manifest['Sha256'][:12].lower())
if args.bundle:
    bundle = args.bundle.resolve()
    manifest = {**manifest,'Tag':'local-candidate','Commit':subprocess.check_output(['git','rev-parse','HEAD'],cwd=repo,text=True).strip()}
server_exe = bundle/'server/RimTogether.Server.exe'
payload = bundle/'packages/guard'
run = build/('soak-'+dt.datetime.now(dt.timezone.utc).strftime('%Y%m%d-%H%M%S'))
run.mkdir(parents=True)
print('SOAK_RUN='+str(run), flush=True)
settings = json.loads((app_root.parent/'settings.json').read_text(encoding='utf-8-sig'))
seed = run/'seed.zip'
shutil.copyfile(settings['SeedReplay'], seed)
password = 'isolated-soak-test-only'
(run/'config.json').write_text(json.dumps({'Username':'SoakHost','HostAddress':'127.0.0.1',
    'Password':password,'SeedReplay':str(seed),'FastRecovery':True}), encoding='utf-8')
mod_root = game_root/'Mods/RimTogether-Multiplayer'
assert mod_root.resolve().is_relative_to(game_root), 'Mod path must not redirect to a real game'
for relative in json.loads((payload/'manifest.json').read_text(encoding='utf-8-sig'))['Files']:
    destination = mod_root/relative; destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(payload/relative, destination)
shutil.copyfile(payload/'manifest.json', mod_root/'About/RimTogetherGuard.json')
harness_root = game_root/'Mods/RimTogether-SoakHarness'
assert harness_root.resolve().is_relative_to(game_root), 'Harness must remain in the isolated game'
(harness_root/'About').mkdir(parents=True, exist_ok=True)
(harness_root/'Assemblies').mkdir(exist_ok=True)
shutil.copyfile(repo/'Testing/SoakHarness/About.xml', harness_root/'About/About.xml')
shutil.copyfile(repo/'Testing/SoakHarness/bin/Release/net48/RimTogether.SoakHarness.dll', harness_root/'Assemblies/RimTogether.SoakHarness.dll')
for role in ('host','guest'):
    data = run/(role+'-data')
    shutil.copytree(build/'unity-data/Config', data/'Config')
    config_path = data/'Config/ModsConfig.xml'
    tree = ET.parse(config_path); active = tree.getroot().find('activeMods')
    if not any(li.text == 'rimtogether.soakharness' for li in active):
        ET.SubElement(active,'li').text = 'rimtogether.soakharness'
    tree.write(config_path, encoding='utf-8', xml_declaration=True)

started_at = dt.datetime.now(dt.timezone.utc).isoformat()
result = {'status':'RUNNING','started_utc':started_at,'version':manifest['Tag'],
    'commit':manifest['Commit'],'server_sha256':hashlib.sha256(server_exe.read_bytes()).hexdigest(),
    'before_requested_seconds':args.before_seconds,'after_requested_seconds':args.after_seconds,
    'transport':'TCP world transfer / UDP commands over loopback; two real Unity clients on one PC',
    'run_directory':str(run),'events':[]}
server_lines = []; games = {}; control_id = 0
server = subprocess.Popen([str(server_exe), str(run/'config.json')], stdin=subprocess.PIPE,
    stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding='utf-8', errors='replace',
    creationflags=subprocess.CREATE_NO_WINDOW)
def server_reader():
    with (run/'server.log').open('w', encoding='utf-8') as output:
        for line in server.stdout:
            server_lines.append(line); output.write(line); output.flush()
threading.Thread(target=server_reader, daemon=True).start()
def text(role):
    path = run/(role+'-player.log')
    return path.read_text(encoding='utf-8', errors='replace') if path.exists() else ''
def event(name, **values):
    entry = {'event':name,'utc':dt.datetime.now(dt.timezone.utc).isoformat(),**values}
    result['events'].append(entry)
    print(json.dumps(entry,ensure_ascii=False), flush=True)
    (run/'result.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
def control(role, command):
    global control_id
    control_id += 1
    target = run/(role+'.control')
    temporary = run/(role+'.control.'+str(control_id)+'.tmp')
    temporary.write_text(str(control_id)+'|'+command, encoding='utf-8')
    # C# ReadAllText holds a brief Windows read-sharing lock. Never expose a
    # partially written command; wait for that reader and atomically replace it.
    try:
        for attempt in range(40):
            try:
                os.replace(temporary,target)
                return
            except PermissionError:
                if attempt == 39: raise
                time.sleep(0.05)
    finally:
        temporary.unlink(missing_ok=True)
def start_client(role):
    env = os.environ.copy(); env.update({'RIMTOGETHER_FAST_RESYNC':'1',
        'RIMTOGETHER_SOAK_ROOT':str(run),'RIMTOGETHER_SOAK_ROLE':role,
        'RIMTOGETHER_SOAK_PASSWORD':password})
    name = 'SoakHost' if role == 'host' else 'SoakGuest'
    mode = ['-batchmode'] if args.batchmode else ['-screen-width','640','-screen-height','480','-screen-fullscreen','0']
    games[role] = subprocess.Popen([str(game_root/'RimWorldWin64.exe'),*mode,
        '-savedatafolder='+str(run/(role+'-data')),'-logfile',str(run/(role+'-player.log')),
        '-connect=127.0.0.1:30502','-username='+name],cwd=game_root,env=env,
        stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,creationflags=subprocess.CREATE_NO_WINDOW)
    event('client_started',role=role,pid=games[role].pid)
def states(role):
    path=run/(role+'-states.tsv'); output={}
    if path.exists():
        for line in path.read_text(encoding='utf-8').splitlines():
            fields=line.split('\t')
            if len(fields)==4: output[int(fields[0])]={'verified':int(fields[1]),'map_ticks':int(fields[2]),'hash':fields[3]}
    return output
def poll_health():
    if server.poll() is not None: raise RuntimeError('Production server exited')
    for role, process in games.items():
        if process.poll() is not None: raise RuntimeError(role+' game exited')
        if 'SOAK HARNESS ERROR:' in text(role): raise RuntimeError(role+' test harness error')
        if 'проверка синхронизации потеряна' in text(role): raise RuntimeError(role+' lost verification')
    if any('Disconnected (ServerPacketRead)' in line for line in server_lines):
        raise RuntimeError('Server rejected a client packet during the test; inspect server.log')
def wait_until(condition, timeout, label):
    deadline=time.monotonic()+timeout
    while time.monotonic()<deadline:
        poll_health()
        if condition(): return
        time.sleep(1)
    raise RuntimeError('Timed out: '+label)
def compare_window(seconds, phase, minimum_tick=0):
    start=time.monotonic(); last_status=start-60; comparisons=set(); initial_map_ticks=None
    initial_desyncs=text('guest').count('Desynced after last valid tick')
    last_resume=0
    while time.monotonic()-start<seconds:
        poll_health()
        if text('guest').count('Desynced after last valid tick')!=initial_desyncs:
            raise RuntimeError('Unexpected desync during '+phase)
        host=states('host'); guest=states('guest')
        common=sorted(tick for tick in host.keys() & guest.keys() if tick>minimum_tick)
        for tick in common:
            if host[tick]['hash']!=guest[tick]['hash']: raise RuntimeError(f'World fingerprint differs during {phase}, tick {tick}')
            comparisons.add(tick)
        if common:
            if initial_map_ticks is None: initial_map_ticks=host[common[-1]]['map_ticks']
            result[phase+'_latest_tick']=common[-1]
            result[phase+'_map_ticks_advanced']=host[common[-1]]['map_ticks']-initial_map_ticks
        if time.monotonic()-last_resume>60:
            control('host','speed|1'); last_resume=time.monotonic()
        if time.monotonic()-last_status>=60:
            event('soak_progress',phase=phase,elapsed_seconds=round(time.monotonic()-start),
                matching_fingerprints=len(comparisons),latest_tick=common[-1] if common else None,
                guest_native_verified_windows=text('guest').count('RimTogether SYNC verified tick='))
            last_status=time.monotonic()
        time.sleep(1)
    if len(comparisons)<min(10,max(2,seconds//10)): raise RuntimeError('Insufficient matching tick samples during '+phase)
    if result.get(phase+'_map_ticks_advanced',0)<max(300,seconds*5):
        raise RuntimeError('Simulation did not advance enough during '+phase)
    result[phase+'_seconds']=round(time.monotonic()-start,2)
    result[phase+'_matching_fingerprints']=len(comparisons)

try:
    deadline=time.monotonic()+20
    while not any(line.startswith('READY ') for line in server_lines):
        if server.poll() is not None or time.monotonic()>deadline: raise RuntimeError('Production server not ready')
        time.sleep(0.25)
    start_client('host')
    wait_until(lambda:'RimTogether SYNC published authority opinion' in text('host'),240,'host publication')
    start_client('guest')
    wait_until(lambda:'RimTogether SYNC verified tick=' in text('guest'),240,'guest first comparison')
    event('both_clients_verified')
    control('host','speed|1')
    compare_window(args.before_seconds,'before')
    upload_count=sum('Got world upload ' in line for line in server_lines)
    tcp_count=sum('BULK TCP:' in line for line in server_lines)
    control('guest','inject')
    wait_until(lambda:(run/'fault.tsv').exists(),30,'fault injection boundary')
    injected_at=time.monotonic()
    fault=(run/'fault.tsv').read_text().strip().split('\t')
    fault_tick=int(fault[1]); result['fault']={'tick':fault_tick,'thing_id':int(fault[2]),
        'hitpoints_before':int(fault[3]),'hitpoints_after':int(fault[4]),'before_hash':fault[5],'after_hash':fault[6]}
    if fault[5]==fault[6]: raise RuntimeError('Injected fault did not alter world fingerprint')
    wait_until(lambda:fault_tick in states('host'),15,'host sample at injection tick')
    if states('host')[fault_tick]['hash']!=fault[5]: raise RuntimeError('Guest baseline was already different before injected fault')
    event('world_mutated_guest_only',**result['fault'])
    wait_until(lambda:'Desynced after last valid tick' in text('guest'),30,'native desync detection')
    result['detection_seconds_approx']=round(time.monotonic()-injected_at,2)
    event('native_desync_detected',seconds_approx=result['detection_seconds_approx'])
    wait_until(lambda:'Multiplayer: rejoining' in text('guest'),30,'automatic recovery without clicking')
    event('automatic_recovery_started')
    wait_until(lambda:text('guest').count('SYNC world-load ms=')>=2,120,'recovery world reload')
    control('host','speed|1')
    wait_until(lambda:'RimTogether SYNC verified tick=' in text('guest').split('Multiplayer: rejoining',1)[1],60,'verification after recovery')
    def same_again():
        host=states('host'); guest=states('guest')
        return any(tick>fault_tick and host[tick]['hash']==guest[tick]['hash'] for tick in host.keys()&guest.keys())
    wait_until(same_again,30,'equal post-recovery world fingerprints')
    if sum('Got world upload ' in line for line in server_lines)<=upload_count: raise RuntimeError('Recovery reused old snapshot instead of healthy host upload')
    if sum('BULK TCP:' in line for line in server_lines)<=tcp_count: raise RuntimeError('Recovery did not use TCP snapshot transfer')
    result['recovered_same_world_seconds_approx']=round(time.monotonic()-injected_at,2)
    result['desync_reports']=len(list((run/'guest-data/MpDesyncs').glob('*.zip')))
    event('same_world_restored',seconds_approx=result['recovered_same_world_seconds_approx'],reports=result['desync_reports'])
    compare_window(args.after_seconds,'after',fault_tick)
    result['status']='PASS'; event('test_passed')
except BaseException as error:
    result['status']='FAIL'; result['error']=repr(error); event('test_failed',error=repr(error))
    raise
finally:
    for process in games.values():
        if process.poll() is None: process.terminate(); process.wait(timeout=15)
    if server.poll() is None:
        server.stdin.write('stop\n'); server.stdin.flush()
        try: server.wait(timeout=20)
        except subprocess.TimeoutExpired: server.kill(); server.wait()
    result['finished_utc']=dt.datetime.now(dt.timezone.utc).isoformat()
    (run/'result.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    print('SOAK_RESULT='+str(run/'result.json'),flush=True)

"""Deploy only this launcher's isolated Hostinger folders using verified FTPS.

Saved account secrets stay in FileZilla; owner credentials and deployment hashes
stay in the ignored .state directory. Other websites and jobs are never written.
"""
from pathlib import Path, PurePosixPath
import base64, ftplib, hashlib, io, json, os, secrets, ssl, urllib.request
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
STATE = ROOT / '.state'
DOMAIN = '/domains/kiddabob.co.uk'
PUBLIC = DOMAIN + '/public_html/BridgeCrew'
PRIVATE = DOMAIN + '/bridgecrew-launcher'
STATE.mkdir(exist_ok=True)
owner_path = STATE / 'hostinger-owner.json'
if owner_path.exists():
    owner = json.loads(owner_path.read_text())
else:
    owner = {'api': 'https://kiddabob.co.uk/BridgeCrew/api/v1/', 'adminKey': secrets.token_urlsafe(32)}
    owner_path.write_text(json.dumps(owner, indent=2))
admin_hash = hashlib.sha256(owner['adminKey'].encode()).hexdigest()
config = ("<?php return ['admin_hash'=>'" + admin_hash + "','max_blob'=>67108864,'max_user_bytes'=>268435456,'max_total_bytes'=>2147483648];\n").encode()
manifest_path = STATE / 'hostinger-deployment.json'
previous = json.loads(manifest_path.read_text()) if manifest_path.exists() else {}
baseline_path = ROOT / 'artifacts/hostinger-site-baseline.json'
if not baseline_path.exists():
    raise RuntimeError('Existing-site baseline required before deployment.')
baseline = json.loads(baseline_path.read_text())
files = {
    PRIVATE + '/app.php': (ROOT / 'Backend/private/app.php').read_bytes(),
    PRIVATE + '/schema.sql': (ROOT / 'Backend/private/schema.sql').read_bytes(),
    PRIVATE + '/config.php': config,
    PUBLIC + '/.htaccess': (ROOT / 'Backend/public/.htaccess').read_bytes(),
    PUBLIC + '/index.php': (ROOT / 'Backend/public/index.php').read_bytes(),
    PUBLIC + '/privacy.html': (ROOT / 'Backend/public/privacy.html').read_bytes(),
}
sha = lambda b: hashlib.sha256(b).hexdigest()
profile = ET.parse(Path(os.environ['APPDATA']) / 'FileZilla/sitemanager.xml')
servers = [s for s in profile.findall('.//Server') if s.findtext('Name') == 'Kiddabob.co.uk']
if len(servers) != 1:
    raise RuntimeError('Expected one saved Hostinger profile.')
s = servers[0]

def read(ftp, path):
    b = io.BytesIO(); ftp.retrbinary('RETR ' + path, b.write); return b.getvalue()

with ftplib.FTP_TLS(context=ssl.create_default_context()) as ftp:
    ftp.connect(s.findtext('Host'), int(s.findtext('Port')), timeout=25)
    # Validate Hostinger's signed service identity, keeping CA/hostname checks on.
    ftp.host = 'hstgr.io'; ftp.auth()
    ftp.login(s.findtext('User'), base64.b64decode(s.findtext('Pass')).decode()); ftp.prot_p()
    if ftp.pwd() != DOMAIN + '/public_html':
        raise RuntimeError('Unexpected hosting root.')
    for path, digest in baseline.items():
        if sha(read(ftp, path)) != digest:
            raise RuntimeError('Existing site changed since baseline; inspect before deployment: ' + path)
    existing = {}
    for remote, data in files.items():
        names = {PurePosixPath(p).name for p in ftp.nlst(str(PurePosixPath(remote).parent))}
        old = read(ftp, remote) if PurePosixPath(remote).name in names else None
        permitted_probe = remote == PUBLIC + '/index.php' and old == (ROOT / 'artifacts/hostinger-probe.php').read_bytes() if (ROOT / 'artifacts/hostinger-probe.php').exists() else False
        if old is not None and old != data and previous.get(remote) != sha(old) and not permitted_probe:
            raise RuntimeError('Untracked remote change: ' + remote)
        existing[remote] = old
    for remote, data in files.items():
        if existing[remote] == data:
            continue
        temp = remote + '.new-' + secrets.token_hex(6)
        ftp.storbinary('STOR ' + temp, io.BytesIO(data))
        if read(ftp, temp) != data:
            raise RuntimeError('Upload checksum mismatch.')
        ftp.sendcmd('SITE CHMOD ' + ('600' if remote.startswith(PRIVATE) else '644') + ' ' + temp)
        ftp.rename(temp, remote)
    for path, digest in baseline.items():
        if sha(read(ftp, path)) != digest:
            raise RuntimeError('Existing-site integrity check failed.')
    for remote, data in files.items():
        if read(ftp, remote) != data:
            raise RuntimeError('Published file mismatch: ' + remote)
manifest_path.write_text(json.dumps({k: sha(v) for k, v in files.items()}, indent=2))
with urllib.request.urlopen(owner['api'] + 'health', timeout=20) as response:
    health = json.load(response)
if health.get('mode') != 'live-service' or health.get('gameConnected') is not False:
    raise RuntimeError('Unexpected deployed service health.')
print('Verified isolated Hostinger deployment, HTTPS health and unchanged existing-site hashes.')
print('API: ' + owner['api'])

<?php
declare(strict_types=1);

// Stateless PHP requests, one private SQLite database, and private blob storage.
// No background daemon or changes to the parent site's configuration are needed.
umask(0077);
ini_set('display_errors', '0');
ini_set('memory_limit', '128M');
set_time_limit(30);
header('Content-Type: application/json; charset=utf-8');
header('Cache-Control: no-store');
header('X-Content-Type-Options: nosniff');
header('Referrer-Policy: no-referrer');

final class ApiError extends RuntimeException {
    public function __construct(public int $status, string $message) { parent::__construct($message); }
}
function fail(int $status, string $message): never { throw new ApiError($status, $message); }
function output(mixed $value, int $status = 200): never {
    http_response_code($status);
    echo json_encode($value, JSON_THROW_ON_ERROR | JSON_UNESCAPED_SLASHES);
    exit;
}
function text(mixed $value, int $max, string $field): string {
    if (!is_string($value) || trim($value) === '' || strlen($value) > $max || preg_match('/[\x00-\x08\x0b\x0c\x0e-\x1f]/', $value)) fail(400, "Invalid $field.");
    return $value;
}
function is_id(mixed $value): bool {
    return is_string($value) && preg_match('/^[a-z0-9][a-z0-9._-]{0,63}$/D', $value) === 1 && safe_path($value);
}
function is_hash(mixed $value): bool { return is_string($value) && preg_match('/^[a-f0-9]{64}$/D', $value) === 1; }
function safe_path(mixed $value): bool {
    if (!is_string($value) || $value === '' || strlen($value) > 240 || preg_match('~[\\\\:\x00-\x1f*?"<>|]|^/~', $value)) return false;
    foreach (explode('/', $value) as $part) {
        if ($part === '' || $part === '.' || $part === '..' || preg_match('/[. ]$/D', $part) || preg_match('/^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)/i', $part)) return false;
    }
    return true;
}
function fields(array $value, array $expected): void {
    $keys = array_keys($value); sort($keys); sort($expected);
    if ($keys !== $expected) fail(400, 'Missing or unknown fields.');
}
function body(): array {
    if (!str_starts_with(strtolower($_SERVER['CONTENT_TYPE'] ?? ''), 'application/json')) fail(415, 'Use application/json.');
    if ((int)($_SERVER['CONTENT_LENGTH'] ?? 0) > 1048576) fail(413, 'Metadata is too large.');
    $stream = fopen('php://input', 'rb'); $raw = stream_get_contents($stream, 1048577); fclose($stream);
    if (strlen($raw) > 1048576) fail(413, 'Metadata is too large.');
    $value = json_decode($raw, true, 64, JSON_THROW_ON_ERROR);
    if (!is_array($value) || array_is_list($value)) fail(400, 'Expected a JSON object.');
    return $value;
}
function dbq(string $sql, array $args = []): PDOStatement {
    global $db; $q = $db->prepare($sql); $q->execute($args); return $q;
}
function row(string $sql, array $args = []): ?array { return dbq($sql, $args)->fetch(PDO::FETCH_ASSOC) ?: null; }
function atomic(callable $work): mixed {
    global $db; $db->exec('BEGIN IMMEDIATE');
    try { $result = $work(); $db->exec('COMMIT'); return $result; }
    catch (Throwable $e) { $db->exec('ROLLBACK'); throw $e; }
}
function token(): string { return rtrim(strtr(base64_encode(random_bytes(32)), '+/', '-_'), '='); }
function now_iso(int $unix): string { return gmdate('Y-m-d\TH:i:s\Z', $unix); }
function public_user(array $user): array { return ['id'=>$user['id'], 'username'=>$user['username'], 'displayName'=>$user['display_name'], 'canPublish'=>(bool)$user['publisher']]; }
function password_check(mixed $password): string {
    if (!is_string($password) || strlen($password)>512 || ($count=preg_match_all('/./us',$password))===false || $count<12 || $count>128) fail(400, 'Use a password of 12 to 128 characters.');
    return $password;
}
function password_hash_new(string $password): string { return password_hash($password, PASSWORD_ARGON2ID, ['memory_cost'=>32768, 'time_cost'=>3, 'threads'=>1]); }
function start_session(array $user, ?string $recovery = null): array {
    $secret = token(); $expires = time() + 7*86400;
    dbq('DELETE FROM sessions WHERE expires <= ?', [time()]);
    dbq('INSERT INTO sessions(hash,user_id,expires,created) VALUES(?,?,?,?)', [hash('sha256',$secret),$user['id'],$expires,time()]);
    dbq('DELETE FROM sessions WHERE user_id=? AND hash NOT IN (SELECT hash FROM sessions WHERE user_id=? ORDER BY created DESC LIMIT 10)', [$user['id'],$user['id']]);
    return ['user'=>public_user($user),'accessToken'=>$secret,'expiresAt'=>now_iso($expires),'recoveryCode'=>$recovery];
}
function authenticate(): array {
    $header = $_SERVER['HTTP_AUTHORIZATION'] ?? '';
    if (!preg_match('/^Bearer ([A-Za-z0-9_-]{43})$/D', $header, $m)) fail(401, 'Sign in to continue.');
    $u = row('SELECT u.*,s.hash AS session_hash FROM sessions s JOIN users u ON u.id=s.user_id WHERE s.hash=? AND s.expires>? AND u.disabled=0', [hash('sha256',$m[1]),time()]);
    if (!$u) fail(401, 'Your session expired. Sign in again.');
    return $u;
}
function rate(string $category, int $limit, int $seconds, string $identity = ''): void {
    global $config;
    $bucket = (int)floor(time()/$seconds);
    $key = hash_hmac('sha256', $category.'|'.($identity!==''?$identity:($_SERVER['REMOTE_ADDR'] ?? '')), $config['rate_salt']);
    $hits = atomic(function() use($key,$bucket,$seconds) {
        dbq('INSERT INTO rates(key,bucket,hits,expires) VALUES(?,?,1,?) ON CONFLICT(key,bucket) DO UPDATE SET hits=hits+1', [$key,$bucket,($bucket+1)*$seconds]);
        return (int)row('SELECT hits FROM rates WHERE key=? AND bucket=?', [$key,$bucket])['hits'];
    });
    if ($hits > $limit) { header('Retry-After: '.$seconds); fail(429, 'Too many requests. Try again later.'); }
}
function canonical(mixed $value): string {
    if (is_array($value)) {
        if (array_is_list($value)) return '['.implode(',',array_map('canonical',$value)).']';
        ksort($value,SORT_STRING); $parts=[];
        foreach ($value as $k=>$v) $parts[]=canonical((string)$k).':'.canonical($v);
        return '{'.implode(',',$parts).'}';
    }
    // Match the existing .NET manifest lock's default JavaScript encoder.
    $json = json_encode($value,JSON_THROW_ON_ERROR|JSON_UNESCAPED_SLASHES|JSON_HEX_TAG|JSON_HEX_AMP|JSON_HEX_APOS|JSON_HEX_QUOT);
    if(is_string($value)) $json = str_replace(['+','`'],['\\u002B','\\u0060'],$json);
    $encoded='';
    for($i=0,$length=strlen($json);$i<$length;$i++) {
        if($json[$i]!=="\\"){$encoded.=$json[$i];continue;}
        $encoded.="\\";$i++;
        if($json[$i]==='u'){$encoded.='u'.strtoupper(substr($json,$i+1,4));$i+=4;}
        else $encoded.=$json[$i];
    }
    return $encoded;
}
function validate_pack(array $p): array {
    fields($p,['schemaVersion','id','version','name','description','loaderProtocol','compatibilityGroup','supportedBuilds','mods']);
    if ($p['schemaVersion']!==1 || $p['loaderProtocol']!==1 || !is_id($p['id']) || !is_id($p['compatibilityGroup'])) fail(400,'Unsupported pack schema, protocol or ID.');
    $version = fn($v)=>is_string($v)&&strlen($v)<=80&&preg_match('/^[0-9]+\.[0-9]+\.[0-9]+(?:-[a-zA-Z0-9.-]+)?$/D',$v);
    if (!$version($p['version'])) fail(400,'Invalid pack version.');
    text($p['name'],120,'pack name');text($p['description'],8000,'pack description');
    if (!is_array($p['supportedBuilds']) || count($p['supportedBuilds'])<1 || count($p['supportedBuilds'])>2) fail(400,'Invalid supported builds.');
    foreach ($p['supportedBuilds'] as $edition=>$builds) {
        if (!in_array($edition,['meta','steam'],true) || !is_array($builds) || !array_is_list($builds) || count($builds)<1 || count($builds)>16) fail(400,'Invalid edition/build list.');
        foreach($builds as $b) text($b,160,'build');
    }
    if (!is_array($p['mods']) || !array_is_list($p['mods']) || count($p['mods'])<1 || count($p['mods'])>128) fail(400,'Invalid mods.');
    $seen=[]; $blobs=[]; $count=0; $total=0; $paths=[];
    foreach($p['mods'] as $mod) {
        if(!is_array($mod))fail(400,'Invalid mod.');
        fields($mod,['id','version','name','author','description','containsCode','dependencies','files']);
        if(!is_id($mod['id']) || !$version($mod['version']) || isset($seen[$mod['id']]) || !is_bool($mod['containsCode']))fail(400,'Invalid or duplicate mod.');
        text($mod['name'],120,'mod name');text($mod['author'],120,'author');text($mod['description'],8000,'description');
        if(!is_array($mod['dependencies']) || !array_is_list($mod['dependencies']) || count($mod['dependencies'])>128)fail(400,'Invalid dependencies.');
        foreach($mod['dependencies'] as $d) {
            if(!is_array($d))fail(400,'Invalid dependency.');fields($d,['id','version']);
            if(!is_string($d['id']) || !isset($seen[$d['id']]) || $seen[$d['id']]!==$d['version'])fail(400,'Dependencies must be pinned in load order.');
        }
        if(!is_array($mod['files']) || !array_is_list($mod['files']) || count($mod['files'])<1 || count($mod['files'])>512)fail(400,'Invalid files.');
        $provided=[];
        foreach($mod['files'] as $f) {
            if(!is_array($f))fail(400,'Invalid file.');fields($f,['variant','path','sha256','bytes']);
            if(!in_array($f['variant'],['shared','meta','steam'],true) || !safe_path($f['path']) || !is_hash($f['sha256']) || !is_int($f['bytes']) || $f['bytes']<0 || $f['bytes']>2147483648)fail(400,'Invalid file path, hash or size.');
            if(isset($blobs[$f['sha256']]) && $blobs[$f['sha256']]!==$f['bytes'])fail(400,'Conflicting sizes for one content hash.');
            $blobs[$f['sha256']]=$f['bytes'];$total+=$f['bytes'];$count++;
            foreach(array_keys($p['supportedBuilds']) as $edition)if($f['variant']==='shared'||$f['variant']===$edition) {
                $path=strtolower($f['path']);
                if(isset($paths[$edition][$path]))fail(400,'Conflicting file paths.');
                $paths[$edition][$path]=true;$provided[$edition]=true;
            }
        }
        if(count($provided)!==count($p['supportedBuilds']))fail(400,'Each mod needs files for every declared edition.');
        $seen[$mod['id']]=$mod['version'];
    }
    if($count>4096||$total>20*1024*1024*1024)fail(400,'Pack is too large.');
    return $blobs;
}
function load_pack(string $id, string $version): array {
    $p=row('SELECT r.*,u.display_name AS publisher_name FROM releases r JOIN users u ON u.id=r.owner WHERE r.pack_id=? AND r.version=? AND r.hidden=0 AND u.disabled=0',[$id,$version]);
    if(!$p)fail(404,'Pack not found.');return $p;
}
function friends(string $a,string $b): bool { return row('SELECT 1 FROM friends WHERE ((sender=? AND recipient=?) OR (sender=? AND recipient=?)) AND accepted=1',[$a,$b,$b,$a])!==null; }
function blocked(string $a,string $b): bool { return row('SELECT 1 FROM blocks WHERE (owner=? AND target=?) OR (owner=? AND target=?)',[$a,$b,$b,$a])!==null; }
function room(string $id): array {
    $r=row('SELECT l.*,u.display_name AS host_name FROM lobbies l JOIN users u ON u.id=l.host WHERE l.id=? AND l.expires>? AND u.disabled=0',[$id,time()]);
    if(!$r)fail(404,'Lobby is no longer available.');load_pack($r['pack_id'],$r['pack_version']);return $r;
}
function access(array $r, array $u, ?string $invite=null): bool {
    if(blocked($r['host'],$u['id']))return false;
    return $r['host']===$u['id'] || $r['visibility']==='public' ||
        ($r['visibility']==='friends' && friends($r['host'],$u['id'])) ||
        row('SELECT 1 FROM members WHERE lobby=? AND user_id=? AND expires>?',[$r['id'],$u['id'],time()])!==null ||
        ($invite!==null && strlen($invite)<=100 && hash_equals($r['invite_hash'],hash('sha256',$invite)));
}
function advertisement(array $r): array {
    $p=load_pack($r['pack_id'],$r['pack_version']);$manifest=json_decode($p['manifest'],true,64,JSON_THROW_ON_ERROR);
    return ['id'=>$r['id'],'hostName'=>$r['host_name'],'name'=>$r['name'],'packId'=>$r['pack_id'],'packVersion'=>$r['pack_version'],
        'packLock'=>$p['pack_lock'],'compatibilityGroup'=>$manifest['compatibilityGroup'],'loaderProtocol'=>1,'revision'=>(int)$r['revision'],'expiresAt'=>now_iso((int)$r['expires']),'isPreview'=>false];
}
function room_view(array $r): array {
    $members=dbq('SELECT u.id,u.username,u.display_name,m.edition,m.build,m.ready,m.pack_lock FROM members m JOIN users u ON u.id=m.user_id WHERE m.lobby=? AND m.expires>? AND u.disabled=0 ORDER BY m.joined',[$r['id'],time()])->fetchAll(PDO::FETCH_ASSOC);
    return ['advertisement'=>advertisement($r),'hostId'=>$r['host'],'visibility'=>$r['visibility'],'capacity'=>(int)$r['capacity'],
        'members'=>array_map(fn($m)=>['userId'=>$m['id'],'username'=>$m['username'],'displayName'=>$m['display_name'],'edition'=>$m['edition'],'build'=>$m['build'],'ready'=>(bool)$m['ready'],'packLock'=>$m['pack_lock']],$members),
        'gameConnected'=>false,'status'=>'launcher-room-only'];
}
function member_plan(array $r,array $b): array {
    if(($b['revision']??null)!==(int)$r['revision'])fail(409,'Lobby changed. Refresh and review its pack.');
    $p=load_pack($r['pack_id'],$r['pack_version']);
    if(($b['packLock']??null)!==$p['pack_lock'])fail(409,'Review and subscribe to the current pack.');
    $manifest=json_decode($p['manifest'],true,64,JSON_THROW_ON_ERROR);
    $edition=$b['edition']??null;$build=$b['build']??null;
    if(!is_string($edition)||!is_string($build)||!in_array($build,$manifest['supportedBuilds'][$edition]??[],true))fail(400,'This pack has not declared the selected edition/build.');
    return [$edition,$build,$p['pack_lock']];
}
function admin(): void {
    global $config;
    $key=$_SERVER['HTTP_X_BRIDGECREW_ADMIN']??'';
    if(strlen($key)!==43 || !isset($config['admin_hash']) || !hash_equals($config['admin_hash'],hash('sha256',$key)))fail(403,'Administrator access required.');
}

try {
    if(PHP_VERSION_ID<80300)fail(503,'This service requires PHP 8.3 or newer.');
    $local=in_array($_SERVER['REMOTE_ADDR']??'', ['127.0.0.1','::1'],true);
    if(!$local && ($_SERVER['HTTPS']??'')!=='on')fail(400,'HTTPS is required.');
    $configFile=getenv('BRIDGECREW_CONFIG_FILE') ?: __DIR__.'/config.php';
    $config=is_file($configFile)?require $configFile:[];
    $data=getenv('BRIDGECREW_DATA_DIR') ?: __DIR__.'/data';
    if(!is_dir($data) && !mkdir($data,0700,true))throw new RuntimeException('Cannot create service data directory.');
    $saltFile=$data.'/rate-salt';
    if(!is_file($saltFile)) { $f=@fopen($saltFile,'x'); if($f){fwrite($f,token());fclose($f);} }
    $config+=['rate_salt'=>file_get_contents($saltFile),'max_blob'=>64*1024*1024,'max_user_bytes'=>256*1024*1024,'max_total_bytes'=>2*1024*1024*1024,'open_registration'=>true,'max_users'=>1000];
    $db=new PDO('sqlite:'.$data.'/service.sqlite',null,null,[PDO::ATTR_ERRMODE=>PDO::ERRMODE_EXCEPTION,PDO::ATTR_DEFAULT_FETCH_MODE=>PDO::FETCH_ASSOC]);
    $db->exec('PRAGMA foreign_keys=ON; PRAGMA busy_timeout=3000;');
    if((int)$db->query('PRAGMA user_version')->fetchColumn()===0) {
        $db->exec('PRAGMA journal_mode=WAL;');
        atomic(function()use($db){$db->exec(file_get_contents(__DIR__.'/schema.sql'));$db->exec('PRAGMA user_version=1;');});
    }
    rate('all',240,60);
    if(random_int(1,100)===1) { dbq('DELETE FROM rates WHERE expires<?',[time()]);dbq('DELETE FROM sessions WHERE expires<?',[time()]);dbq('DELETE FROM lobbies WHERE expires<?',[time()-86400]); }
    $route=trim($_GET['route']??'','/');$method=$_SERVER['REQUEST_METHOD'];
    if($route===''||($route==='health'&&$method==='GET'))output(['service'=>'bridgecrew-launcher','protocol'=>1,'mode'=>'live-service','gameConnected'=>false,'maxUploadBytes'=>$config['max_blob'],'openRegistration'=>$config['open_registration']]);

    if($route==='auth/register'&&$method==='POST') {
        rate('register',5,3600);if(!$config['open_registration'])fail(403,'Registration is currently closed.');
        $b=body();fields($b,['username','displayName','password']);$username=strtolower(text($b['username'],32,'username'));
        if(!preg_match('/^[a-z0-9][a-z0-9_]{2,31}$/D',$username))fail(400,'Username must be 3-32 letters, digits or underscores.');
        $display=text($b['displayName'],64,'display name');$password=password_check($b['password']);$recovery=token();
        $u=['id'=>bin2hex(random_bytes(16)),'username'=>$username,'display_name'=>$display,'publisher'=>0];
        $passwordHash=password_hash_new($password);
        $result=atomic(function()use($u,$passwordHash,$recovery,$config){
            if((int)dbq('SELECT COUNT(*) FROM users')->fetchColumn()>=$config['max_users'])fail(503,'Registration capacity reached.');
            if(row('SELECT 1 FROM users WHERE username=?',[$u['username']]))fail(409,'That username is unavailable.');
            dbq('INSERT INTO users(id,username,display_name,password,recovery_hash,created) VALUES(?,?,?,?,?,?)',[$u['id'],$u['username'],$u['display_name'],$passwordHash,hash('sha256',$recovery),time()]);
            return start_session($u,$recovery);
        });output($result,201);
    }
    if($route==='auth/login'&&$method==='POST') {
        rate('login',20,600);$b=body();fields($b,['username','password']);
        $name=strtolower(text($b['username'],32,'username'));$password=text($b['password'],512,'password');
        rate('account-login',10,600,$name);
        $u=row('SELECT * FROM users WHERE username=? AND disabled=0',[$name]);
        if(!$u || !password_verify($password,$u['password'])) { if(!$u)password_hash_new($password);fail(401,'Incorrect username or password.'); }
        $rehash=password_needs_rehash($u['password'],PASSWORD_ARGON2ID,['memory_cost'=>32768,'time_cost'=>3,'threads'=>1])?password_hash_new($password):null;
        output(atomic(function()use($u,$rehash){$current=row('SELECT password,disabled FROM users WHERE id=?',[$u['id']]);if(!$current||$current['disabled']||$current['password']!==$u['password'])fail(401,'Account changed. Sign in again.');if($rehash!==null)dbq('UPDATE users SET password=? WHERE id=?',[$rehash,$u['id']]);return start_session($u);}));
    }
    if($route==='auth/recover'&&$method==='POST') {
        rate('recover',5,3600);$b=body();fields($b,['username','recoveryCode','password']);
        $name=strtolower(text($b['username'],32,'username'));$code=text($b['recoveryCode'],100,'recovery code');$password=password_check($b['password']);
        $u=row('SELECT * FROM users WHERE username=? AND disabled=0',[$name]);
        if(!$u||!hash_equals($u['recovery_hash'],hash('sha256',$code)))fail(401,'Incorrect username or recovery code.');
        $recovery=token();$hash=password_hash_new($password);
        output(atomic(function()use($u,$recovery,$hash){$updated=dbq('UPDATE users SET password=?,recovery_hash=? WHERE id=? AND recovery_hash=? AND disabled=0',[$hash,hash('sha256',$recovery),$u['id'],$u['recovery_hash']]);if($updated->rowCount()!==1)fail(401,'Recovery code already used.');dbq('DELETE FROM sessions WHERE user_id=?',[$u['id']]);return start_session($u,$recovery);}));
    }
    if(str_starts_with($route,'admin/')) {
        admin();
        if($route==='admin/reports'&&$method==='GET')output(dbq('SELECT r.id,u.username,r.pack_id AS packId,r.version,r.reason,r.created FROM reports r JOIN users u ON u.id=r.user_id ORDER BY r.id DESC LIMIT 100')->fetchAll());
        $b=body();
        if($route==='admin/publisher'&&$method==='POST') {fields($b,['username','enabled']);if(!is_bool($b['enabled']))fail(400,'Invalid permission.');$u=row('SELECT id FROM users WHERE username=?',[strtolower(text($b['username'],32,'username'))]);if(!$u)fail(404,'Account not found.');dbq('UPDATE users SET publisher=? WHERE id=?',[$b['enabled']?1:0,$u['id']]);output(['updated'=>true]);}
        if($route==='admin/hide-pack'&&$method==='POST') {fields($b,['id','version','hidden']);if(!is_bool($b['hidden']))fail(400,'Invalid visibility.');dbq('UPDATE releases SET hidden=? WHERE pack_id=? AND version=?',[$b['hidden']?1:0,text($b['id'],64,'pack'),text($b['version'],80,'version')]);output(['updated'=>true]);}
        if($route==='admin/account'&&$method==='POST') {fields($b,['username','disabled']);if(!is_bool($b['disabled']))fail(400,'Invalid account state.');$u=row('SELECT id FROM users WHERE username=?',[strtolower(text($b['username'],32,'username'))]);if(!$u)fail(404,'Account not found.');atomic(function()use($u,$b){dbq('UPDATE users SET disabled=? WHERE id=?',[$b['disabled']?1:0,$u['id']]);if($b['disabled']){dbq('DELETE FROM sessions WHERE user_id=?',[$u['id']]);dbq('DELETE FROM lobbies WHERE host=?',[$u['id']]);dbq('DELETE FROM members WHERE user_id=?',[$u['id']]);}});output(['updated'=>true]);}
        fail(404,'Unknown admin operation.');
    }
    if(preg_match('~^packs/([^/]+)/([^/]+)$~D',$route,$m)&&$method==='GET')output(json_decode(load_pack($m[1],$m[2])['manifest'],true,64,JSON_THROW_ON_ERROR));
    if(preg_match('~^releases/([^/]+)/([^/]+)$~D',$route,$m)&&$method==='GET'){
        $r=load_pack($m[1],$m[2]);output(['id'=>$r['pack_id'],'version'=>$r['version'],'name'=>$r['name'],'description'=>$r['description'],'packLock'=>$r['pack_lock'],'publisherId'=>$r['owner'],'publisherName'=>$r['publisher_name'],'publishedAt'=>now_iso((int)$r['created'])]);
    }
    if($route==='packs'&&$method==='GET') {
        $query=$_GET['q']??'';if(!is_string($query)||strlen($query)>100)fail(400,'Invalid search.');
        $rows=dbq('SELECT r.*,u.display_name AS publisher_name FROM releases r JOIN users u ON u.id=r.owner WHERE r.hidden=0 AND u.disabled=0 AND (r.pack_id LIKE ? OR r.name LIKE ?) ORDER BY r.created DESC,r.pack_id LIMIT 100',['%'.$query.'%','%'.$query.'%'])->fetchAll();
        output(array_map(fn($r)=>['id'=>$r['pack_id'],'version'=>$r['version'],'name'=>$r['name'],'description'=>$r['description'],'packLock'=>$r['pack_lock'],'publisherId'=>$r['owner'],'publisherName'=>$r['publisher_name'],'publishedAt'=>now_iso((int)$r['created'])],$rows));
    }
    if(preg_match('~^files/([a-f0-9]{64})$~D',$route,$m)&&$method==='GET') {
        $b=row('SELECT b.bytes FROM blobs b WHERE b.hash=? AND EXISTS(SELECT 1 FROM release_files f JOIN releases r ON r.pack_id=f.pack_id AND r.version=f.version JOIN users u ON u.id=r.owner WHERE f.hash=b.hash AND r.hidden=0 AND u.disabled=0)',[$m[1]]);
        $path=$data.'/blobs/'.$m[1];if(!$b||!is_file($path))fail(404,'Content not found.');
        header('Content-Type: application/octet-stream');header('Content-Disposition: attachment; filename="'.$m[1].'"');header('Content-Length: '.$b['bytes']);readfile($path);exit;
    }

    $user=authenticate();
    if($route==='auth/me'&&$method==='GET')output(public_user($user));
    if($route==='auth/logout'&&$method==='POST') {dbq('DELETE FROM sessions WHERE hash=?',[$user['session_hash']]);output(['signedOut'=>true]);}
    if($route==='auth/password'&&$method==='POST') {
        rate('password',5,3600,$user['id']);$b=body();fields($b,['currentPassword','password']);
        if(!password_verify(text($b['currentPassword'],512,'current password'),$user['password']))fail(401,'Incorrect current password.');
        $hash=password_hash_new(password_check($b['password']));$recovery=token();
        output(atomic(function()use($user,$hash,$recovery){$updated=dbq('UPDATE users SET password=?,recovery_hash=? WHERE id=? AND password=? AND disabled=0',[$hash,hash('sha256',$recovery),$user['id'],$user['password']]);if($updated->rowCount()!==1)fail(401,'Account changed. Sign in again.');dbq('DELETE FROM sessions WHERE user_id=?',[$user['id']]);return start_session($user,$recovery);}));
    }
    if(preg_match('~^uploads/([a-f0-9]{64})$~D',$route,$m)&&$method==='PUT') {
        if(!$user['publisher'])fail(403,'Publishing access must be granted by the service owner.');rate('uploads',20,600,$user['id']);
        $length=filter_var($_SERVER['CONTENT_LENGTH']??null,FILTER_VALIDATE_INT);
        if($length===false||$length===null||$length<0||$length>$config['max_blob'])fail(413,'Upload is missing its length or exceeds the upload limit.');
        if(!is_dir($data.'/blobs'))mkdir($data.'/blobs',0700);
        $temp=$data.'/blobs/'.bin2hex(random_bytes(16)).'.part';$target=$data.'/blobs/'.$m[1];
        try {
            $input=fopen('php://input','rb');$dest=fopen($temp,'xb');$digest=hash_init('sha256');$bytes=0;
            try {while(!feof($input)){$chunk=fread($input,65536);if($chunk===false)throw new RuntimeException('Upload read failed.');$bytes+=strlen($chunk);if($bytes>$length)fail(400,'Upload exceeds its declared length.');hash_update($digest,$chunk);if(fwrite($dest,$chunk)!==strlen($chunk))throw new RuntimeException('Upload write failed.');}}finally{fclose($input);fclose($dest);}
            if($bytes!==$length||!hash_equals($m[1],hash_final($digest)))fail(400,'Upload failed its length/hash check.');
            atomic(function()use($user,$m,$bytes,$config,$target,$temp){
                $owns=row('SELECT 1 FROM user_blobs WHERE user_id=? AND hash=?',[$user['id'],$m[1]]);
                $used=(int)dbq('SELECT COALESCE(SUM(b.bytes),0) FROM user_blobs ub JOIN blobs b ON b.hash=ub.hash WHERE ub.user_id=?',[$user['id']])->fetchColumn();
                $total=(int)dbq('SELECT COALESCE(SUM(bytes),0) FROM blobs')->fetchColumn();$existing=row('SELECT bytes FROM blobs WHERE hash=?',[$m[1]]);
                if((!$owns&&$used+$bytes>$config['max_user_bytes'])||(!$existing&&$total+$bytes>$config['max_total_bytes']))fail(413,'Hosting storage quota reached.');
                if(!$existing){if(!rename($temp,$target))throw new RuntimeException('Cannot commit content.');dbq('INSERT INTO blobs(hash,bytes,created) VALUES(?,?,?)',[$m[1],$bytes,time()]);}
                dbq('INSERT OR IGNORE INTO user_blobs(user_id,hash) VALUES(?,?)',[$user['id'],$m[1]]);
            });if(is_file($temp))unlink($temp);output(['sha256'=>$m[1],'bytes'=>$bytes,'verified'=>true],201);
        }finally{if(is_file($temp))unlink($temp);}
    }
    if($route==='packs'&&$method==='POST') {
        if(!$user['publisher'])fail(403,'Publishing access must be granted by the service owner.');rate('publish',20,3600,$user['id']);
        $b=body();$blobs=validate_pack($b);$json=canonical($b);$lock=hash('sha256',$json);
        atomic(function()use($b,$blobs,$json,$lock,$user){
            $owner=row('SELECT owner FROM pack_ids WHERE id=?',[$b['id']]);if($owner&&$owner['owner']!==$user['id'])fail(403,'This pack ID belongs to another publisher.');
            if(row('SELECT 1 FROM releases WHERE pack_id=? AND version=?',[$b['id'],$b['version']]))fail(409,'Published versions are immutable. Publish a new version.');
            if((int)dbq('SELECT COUNT(*) FROM releases WHERE owner=?',[$user['id']])->fetchColumn()>=100)fail(413,'Publisher release quota reached.');
            foreach($blobs as $hash=>$bytes){$owned=row('SELECT b.bytes FROM user_blobs u JOIN blobs b ON b.hash=u.hash WHERE u.user_id=? AND u.hash=?',[$user['id'],$hash]);if(!$owned||(int)$owned['bytes']!==$bytes)fail(400,'Upload and verify every referenced file first.');}
            if(!$owner)dbq('INSERT INTO pack_ids(id,owner) VALUES(?,?)',[$b['id'],$user['id']]);
            dbq('INSERT INTO releases(pack_id,version,owner,name,description,pack_lock,manifest,created) VALUES(?,?,?,?,?,?,?,?)',[$b['id'],$b['version'],$user['id'],$b['name'],$b['description'],$lock,$json,time()]);
            foreach(array_keys($blobs)as$hash)dbq('INSERT INTO release_files(pack_id,version,hash) VALUES(?,?,?)',[$b['id'],$b['version'],$hash]);
        });output(['id'=>$b['id'],'version'=>$b['version'],'packLock'=>$lock],201);
    }
    if($route==='friends'&&$method==='GET') {
        $rows=dbq('SELECT f.*,u.username,u.display_name FROM friends f JOIN users u ON u.id=CASE WHEN f.sender=? THEN f.recipient ELSE f.sender END WHERE (f.sender=? OR f.recipient=?) AND u.disabled=0 ORDER BY u.username LIMIT 200',[$user['id'],$user['id'],$user['id']])->fetchAll();
        output(array_map(fn($r)=>['userId'=>$r['sender']===$user['id']?$r['recipient']:$r['sender'],'username'=>$r['username'],'displayName'=>$r['display_name'],'status'=>$r['accepted']?'accepted':($r['recipient']===$user['id']?'incoming':'outgoing')],$rows));
    }
    if($route==='friends'&&$method==='POST') {
        rate('friend-request',20,3600,$user['id']);$b=body();fields($b,['username']);
        $target=row('SELECT * FROM users WHERE username=? AND disabled=0',[strtolower(text($b['username'],32,'username'))]);
        if(!$target||$target['id']===$user['id']||blocked($user['id'],$target['id']))fail(404,'Account is unavailable.');
        atomic(function()use($user,$target){
            if(row('SELECT 1 FROM friends WHERE (sender=? AND recipient=?) OR (sender=? AND recipient=?)',[$user['id'],$target['id'],$target['id'],$user['id']]))fail(409,'A friendship or request already exists.');
            if((int)dbq('SELECT COUNT(*) FROM friends WHERE sender=? OR recipient=?',[$user['id'],$user['id']])->fetchColumn()>=200)fail(409,'Friend limit reached.');
            dbq('INSERT INTO friends(sender,recipient,accepted) VALUES(?,?,0)',[$user['id'],$target['id']]);
        });output(['requested'=>true],201);
    }
    if(preg_match('~^friends/([a-f0-9]{32})$~D',$route,$m)) {
        if($method==='POST'){if(!row('SELECT 1 FROM friends WHERE sender=? AND recipient=?',[$m[1],$user['id']]))fail(404,'Incoming request not found.');dbq('UPDATE friends SET accepted=1 WHERE sender=? AND recipient=?',[$m[1],$user['id']]);output(['accepted'=>true]);}
        if($method==='DELETE'){dbq('DELETE FROM friends WHERE (sender=? AND recipient=?) OR (sender=? AND recipient=?)',[$user['id'],$m[1],$m[1],$user['id']]);output(['removed'=>true]);}
    }
    if(preg_match('~^blocks/([a-f0-9]{32})$~D',$route,$m)) {
        if($method==='POST') {if($m[1]===$user['id'])fail(400,'Cannot block yourself.');if(!row('SELECT 1 FROM users WHERE id=?',[$m[1]]))fail(404,'Account not found.');atomic(function()use($user,$m){dbq('INSERT OR IGNORE INTO blocks(owner,target) VALUES(?,?)',[$user['id'],$m[1]]);dbq('DELETE FROM friends WHERE (sender=? AND recipient=?) OR (sender=? AND recipient=?)',[$user['id'],$m[1],$m[1],$user['id']]);dbq('DELETE FROM members WHERE (user_id=? AND lobby IN (SELECT id FROM lobbies WHERE host=?)) OR (user_id=? AND lobby IN (SELECT id FROM lobbies WHERE host=?))',[$user['id'],$m[1],$m[1],$user['id']]);});output(['blocked'=>true]);}
        if($method==='DELETE'){dbq('DELETE FROM blocks WHERE owner=? AND target=?',[$user['id'],$m[1]]);output(['unblocked'=>true]);}
    }
    if($route==='presence'&&$method==='GET') {
        $r=row('SELECT l.id FROM members m JOIN lobbies l ON l.id=m.lobby JOIN releases r ON r.pack_id=l.pack_id AND r.version=l.pack_version WHERE m.user_id=? AND m.expires>? AND l.expires>? AND r.hidden=0 ORDER BY m.joined DESC LIMIT 1',[$user['id'],time(),time()]);
        output(['room'=>$r?room_view(room($r['id'])):null]);
    }
    if($route==='lobbies'&&$method==='GET') {
        $q=$_GET['q']??'';if(!is_string($q)||strlen($q)>100)fail(400,'Invalid search.');$out=[];
        // Apply visibility before the result limit so private rooms cannot crowd out public ones.
        $rows=dbq('SELECT l.*,u.display_name AS host_name FROM lobbies l JOIN users u ON u.id=l.host JOIN releases r ON r.pack_id=l.pack_id AND r.version=l.pack_version WHERE l.expires>? AND u.disabled=0 AND r.hidden=0 AND (l.name LIKE ? OR u.display_name LIKE ?) ORDER BY l.created DESC LIMIT 500',[time(),'%'.$q.'%','%'.$q.'%'])->fetchAll();
        foreach($rows as$r)if(access($r,$user)){ $out[]=advertisement($r);if(count($out)===100)break; }output($out);
    }
    if($route==='lobbies'&&$method==='POST') {
        rate('host',20,600,$user['id']);$b=body();fields($b,['name','packId','packVersion','visibility','capacity','edition','build','packLock']);
        text($b['name'],120,'lobby name');if(!in_array($b['visibility'],['public','friends','invite'],true)||!is_int($b['capacity'])||$b['capacity']<1||$b['capacity']>4)fail(400,'Invalid lobby visibility/capacity.');
        $p=load_pack(text($b['packId'],64,'pack'),text($b['packVersion'],80,'version'));$invite=token();$id=bin2hex(random_bytes(16));
        atomic(function()use($b,$user,$invite,$id){
            if(row('SELECT 1 FROM members m JOIN lobbies l ON l.id=m.lobby WHERE m.user_id=? AND m.expires>? AND l.expires>?',[$user['id'],time(),time()]))fail(409,'Leave your current lobby before hosting.');
            $r=['id'=>$id,'host'=>$user['id'],'name'=>$b['name'],'pack_id'=>$b['packId'],'pack_version'=>$b['packVersion'],'revision'=>1];
            [$edition,$build,$lock]=member_plan($r,$b+['revision'=>1]);
            dbq('INSERT INTO lobbies(id,host,name,pack_id,pack_version,visibility,capacity,revision,invite_hash,created,expires) VALUES(?,?,?,?,?,?,?,?,?,?,?)',[$id,$user['id'],$b['name'],$b['packId'],$b['packVersion'],$b['visibility'],$b['capacity'],1,hash('sha256',$invite),time(),time()+120]);
            dbq('INSERT INTO members(lobby,user_id,edition,build,pack_lock,ready,joined,expires) VALUES(?,?,?,?,?,0,?,?)',[$id,$user['id'],$edition,$build,$lock,time(),time()+120]);
        });output(['room'=>room_view(room($id)),'inviteCode'=>$invite],201);
    }
    if($route==='invites/resolve'&&$method==='POST') {
        rate('invite',30,600,$user['id']);$b=body();fields($b,['code']);$code=text($b['code'],100,'invitation');
        $r=row('SELECT id FROM lobbies WHERE invite_hash=? AND expires>?',[hash('sha256',$code),time()]);if(!$r)fail(404,'Invitation is unavailable.');$r=room($r['id']);if(!access($r,$user,$code))fail(404,'Invitation is unavailable.');output(advertisement($r));
    }
    if(preg_match('~^lobbies/([a-f0-9]{32})(?:/(join|ready|heartbeat|leave|invite|kick))?$~D',$route,$m)) {
        $id=$m[1];$action=$m[2]??'';
        if($method==='GET'&&$action===''){ $r=room($id);if(!access($r,$user))fail(404,'Lobby is unavailable.');output(advertisement($r)); }
        if($method==='GET'&&$action==='heartbeat'){ $r=room($id);if(!access($r,$user))fail(404,'Lobby is unavailable.');output(room_view($r)); }
        if($method==='DELETE'&&$action===''){ $r=room($id);if($r['host']!==$user['id'])fail(403,'Only the host can close this lobby.');dbq('DELETE FROM lobbies WHERE id=?',[$id]);output(['closed'=>true]); }
        if($method==='POST'&&$action==='join') {
            $b=body();fields($b,['revision','packLock','edition','build','inviteCode']);
            atomic(function()use($id,$user,$b){
                $r=room($id);if(!access($r,$user,$b['inviteCode']))fail(404,'Lobby is unavailable.');[$edition,$build,$lock]=member_plan($r,$b);
                $existing=row('SELECT 1 FROM members WHERE lobby=? AND user_id=? AND expires>?',[$id,$user['id'],time()]);
                if(!$existing&&(int)dbq('SELECT COUNT(*) FROM members WHERE lobby=? AND expires>?',[$id,time()])->fetchColumn()>=(int)$r['capacity'])fail(409,'Lobby is full.');
                if(row('SELECT 1 FROM members m JOIN lobbies l ON l.id=m.lobby WHERE m.user_id=? AND m.lobby<>? AND m.expires>? AND l.expires>?',[$user['id'],$id,time(),time()]))fail(409,'Leave your current lobby first.');
                dbq('INSERT INTO members(lobby,user_id,edition,build,pack_lock,ready,joined,expires) VALUES(?,?,?,?,?,0,?,?) ON CONFLICT(lobby,user_id) DO UPDATE SET edition=excluded.edition,build=excluded.build,pack_lock=excluded.pack_lock,ready=0,expires=excluded.expires',[$id,$user['id'],$edition,$build,$lock,time(),time()+120]);
            });output(room_view(room($id)));
        }
        if($method==='POST'&&$action==='heartbeat') {
            atomic(function()use($id,$user){$r=room($id);if(!row('SELECT 1 FROM members WHERE lobby=? AND user_id=? AND expires>?',[$id,$user['id'],time()])||!access($r,$user))fail(403,'Join the lobby first.');dbq('UPDATE members SET expires=? WHERE lobby=? AND user_id=?',[time()+120,$id,$user['id']]);if($r['host']===$user['id'])dbq('UPDATE lobbies SET expires=? WHERE id=?',[time()+120,$id]);});output(room_view(room($id)));
        }
        if($method==='POST'&&$action==='ready') {
            $b=body();fields($b,['revision','packLock','ready']);if(!is_bool($b['ready']))fail(400,'Invalid readiness.');
            atomic(function()use($id,$user,$b){$r=room($id);$member=row('SELECT * FROM members WHERE lobby=? AND user_id=? AND expires>?',[$id,$user['id'],time()]);if(!$member||!access($r,$user))fail(403,'Join the lobby first.');member_plan($r,$b+['edition'=>$member['edition'],'build'=>$member['build']]);dbq('UPDATE members SET ready=?,expires=? WHERE lobby=? AND user_id=?',[$b['ready']?1:0,time()+120,$id,$user['id']]);});output(room_view(room($id)));
        }
        if($method==='POST'&&$action==='leave') {
            $r=room($id);if($r['host']===$user['id'])dbq('DELETE FROM lobbies WHERE id=?',[$id]);else dbq('DELETE FROM members WHERE lobby=? AND user_id=?',[$id,$user['id']]);output(['left'=>true]);
        }
        if($method==='POST'&&$action==='invite') {
            $r=room($id);if($r['host']!==$user['id'])fail(403,'Only the host can rotate invitations.');$code=token();dbq('UPDATE lobbies SET invite_hash=? WHERE id=?',[hash('sha256',$code),$id]);output(['inviteCode'=>$code]);
        }
        if($method==='POST'&&$action==='kick') {
            $b=body();fields($b,['userId']);$target=text($b['userId'],32,'member');$r=room($id);
            if($r['host']!==$user['id']||$target===$user['id'])fail(403,'Only the host can remove other crew members.');
            dbq('DELETE FROM members WHERE lobby=? AND user_id=?',[$id,$target]);output(room_view($r));
        }
    }
    if($route==='reports'&&$method==='POST') {
        rate('report',5,3600,$user['id']);$b=body();fields($b,['packId','packVersion','reason']);load_pack(text($b['packId'],64,'pack'),text($b['packVersion'],80,'version'));
        dbq('INSERT INTO reports(user_id,pack_id,version,reason,created) VALUES(?,?,?,?,?)',[$user['id'],$b['packId'],$b['packVersion'],text($b['reason'],2000,'reason'),time()]);output(['received'=>true],201);
    }
    fail(404,'Unknown API operation.');
} catch(ApiError $e) { output(['error'=>$e->getMessage()],$e->status); }
catch(JsonException|TypeError|ValueError $e) { output(['error'=>'Invalid request data.'],400); }
catch(Throwable $e) { error_log('BridgeCrew service: '.get_class($e).': '.$e->getMessage()); output(['error'=>'The service could not complete this request.'],503); }

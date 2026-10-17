<?php
// Local PHP development server only. Production uses the folder's .htaccess.
$_GET['route'] = trim(parse_url($_SERVER['REQUEST_URI'], PHP_URL_PATH), '/');
require __DIR__ . '/public/index.php';

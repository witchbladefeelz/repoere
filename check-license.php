<?php
$host = getenv('DB_HOST') ?: 'localhost';
$dbname = getenv('DB_NAME') ?: 'syntara';
$username = getenv('DB_USER') ?: 'root';
$password = getenv('DB_PASSWORD') ?: 'admin';
$port = getenv('DB_PORT') ?: '3306';

try {
    $dsn = "mysql:host=$host;port=$port;dbname=$dbname;charset=utf8mb4";
    $pdo = new PDO($dsn, $username, $password, [
        PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION,
        PDO::ATTR_DEFAULT_FETCH_MODE => PDO::FETCH_ASSOC,
    ]);
} catch(PDOException $e) {
    http_response_code(500);
    die(json_encode(['success' => false, 'message' => 'Database connection failed']));
}

$hwid = trim($_REQUEST['hwid'] ?? '');

if (empty($hwid)) {
    http_response_code(400);
    die(json_encode(['success' => false, 'message' => 'Missing required parameter: hwid']));
}

if (strlen($hwid) > 255) {
    http_response_code(400);
    die(json_encode(['success' => false, 'message' => 'HWID too long']));
}

try {
    $stmt = $pdo->prepare("SELECT id, subscription, banned FROM users WHERE hwid = ? LIMIT 1");
    $stmt->execute([$hwid]);
    $user = $stmt->fetch(PDO::FETCH_ASSOC);
    
    if (!$user) {
        echo json_encode([
            'success' => false,
            'message' => 'HWID not found',
            'valid' => false,
            'user' => null
        ]);
        exit;
    }
    
    $current_time = time();
    $expiry_time = strtotime($user['subscription']);
    $is_banned = (bool)$user['banned'];
    $is_expired = $expiry_time <= $current_time;
    $is_valid = !$is_banned && !$is_expired;
    
    $response = [
        'success' => true,
        'message' => $is_valid ? 'Subscription valid' : ($is_banned ? 'User banned' : 'Subscription expired'),
        'valid' => $is_valid,
        'user' => [
            'id' => $user['id'],
            'hwid' => $hwid,
            'subscription' => $user['subscription'],
            'banned' => $is_banned,
            'expired' => $is_expired,
            'days_remaining' => $is_valid ? max(0, floor(($expiry_time - $current_time) / 86400)) : 0
        ]
    ];
    
    echo json_encode($response);
    
} catch(PDOException $e) {
    http_response_code(500);
    echo json_encode(['success' => false, 'message' => 'Database error']);
}
?>
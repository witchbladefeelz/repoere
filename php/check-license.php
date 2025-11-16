<?php
declare(strict_types=1);

require __DIR__ . '/config.php';

header('Content-Type: application/json');

$hwid = trim($_REQUEST['hwid'] ?? '');

if ($hwid === '') {
    http_response_code(400);
    echo json_encode(['success' => false, 'message' => 'Missing required parameter: hwid']);
    exit;
}

if (strlen($hwid) > 255) {
    http_response_code(400);
    echo json_encode(['success' => false, 'message' => 'HWID too long']);
    exit;
}

try {
    $stmt = $pdo->prepare("SELECT id, subscription, banned FROM users WHERE hwid = ? LIMIT 1");
    $stmt->execute([$hwid]);
    $user = $stmt->fetch();

    if (!$user) {
        echo json_encode([
            'success' => false,
            'message' => 'HWID not found',
            'valid' => false,
            'user' => null,
        ]);
        exit;
    }

    $currentTime = time();
    $expiryTime = strtotime((string)$user['subscription']);
    $isBanned = (bool)$user['banned'];
    $isExpired = $expiryTime <= $currentTime;
    $isValid = !$isBanned && !$isExpired;

    $response = [
        'success' => true,
        'message' => $isValid ? 'Subscription valid' : ($isBanned ? 'User banned' : 'Subscription expired'),
        'valid' => $isValid,
        'user' => [
            'id' => $user['id'],
            'hwid' => $hwid,
            'subscription' => $user['subscription'],
            'banned' => $isBanned,
            'expired' => $isExpired,
            'days_remaining' => $isValid ? max(0, floor(($expiryTime - $currentTime) / 86400)) : 0,
        ],
    ];

    echo json_encode($response);
} catch (PDOException $e) {
    http_response_code(500);
    echo json_encode(['success' => false, 'message' => 'Database error']);
}


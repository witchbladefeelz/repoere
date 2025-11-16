<?php
$host = getenv('DB_HOST') ?: 'localhost';
$dbname = getenv('DB_NAME') ?: 'syntara';
$username = getenv('DB_USER') ?: 'root';
$password = getenv('DB_PASSWORD') ?: 'admin';
$port = getenv('DB_PORT') ?: '3306';
$bot_token = getenv('BOT_TOKEN') ?: '8541885040:AAHqnkLK4o8TAAnTt4rp1z--JRnl672uMVw';

function send_telegram_message($chat_id, $message, $bot_token) {
    $url = "https://api.telegram.org/bot{$bot_token}/sendMessage";
    $data = [
        'chat_id' => $chat_id,
        'text' => $message,
        'parse_mode' => 'Markdown'
    ];
    
    $ch = curl_init();
    curl_setopt($ch, CURLOPT_URL, $url);
    curl_setopt($ch, CURLOPT_POST, true);
    curl_setopt($ch, CURLOPT_POSTFIELDS, http_build_query($data));
    curl_setopt($ch, CURLOPT_RETURNTRANSFER, true);
    curl_setopt($ch, CURLOPT_SSL_VERIFYPEER, false);
    
    $result = curl_exec($ch);
    curl_close($ch);
    
    return $result;
}

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
$key = trim($_REQUEST['key'] ?? '');

if (empty($hwid) || empty($key)) {
    http_response_code(400);
    die(json_encode(['success' => false, 'message' => 'Missing required parameters: hwid and key']));
}

if (strlen($hwid) > 255 || strlen($key) > 255) {
    http_response_code(400);
    die(json_encode(['success' => false, 'message' => 'Parameters too long']));
}

try {
    $pdo->beginTransaction();
    
    $stmt = $pdo->prepare("SELECT id, days, expires_at FROM subscriptions WHERE subscription_key = ? LIMIT 1");
    $stmt->execute([$key]);
    $subscription = $stmt->fetch(PDO::FETCH_ASSOC);
    
    if (!$subscription) {
        $pdo->rollBack();
        http_response_code(404);
        die(json_encode(['success' => false, 'message' => 'Invalid subscription key']));
    }
    
    $telegram_chat_id = $subscription['id'];
    $days = (int)$subscription['days'];
    $expires_at = !empty($subscription['expires_at']) ? strtotime($subscription['expires_at']) : null;
    
    if ($days <= 0) {
        $pdo->rollBack();
        http_response_code(400);
        die(json_encode(['success' => false, 'message' => 'Invalid subscription duration']));
    }
    
    // If key was never activated (expires_at is null), set it now (first activation)
    // If key was already activated, check if expired and use remaining time
    if ($expires_at === null) {
        // First activation - set expires_at from now
        if ($days >= 99999) {
            $expiry_date = date('Y-m-d H:i:s', strtotime('+100 years'));
            $expires_at_timestamp = strtotime('+100 years');
        } else {
            $expiry_date = date('Y-m-d H:i:s', strtotime("+$days days"));
            $expires_at_timestamp = strtotime("+$days days");
        }
        // Update the key's expires_at in subscriptions table
        $update_stmt = $pdo->prepare("UPDATE subscriptions SET expires_at = ? WHERE subscription_key = ?");
        $update_stmt->execute([date('Y-m-d H:i:s', $expires_at_timestamp), $key]);
    } else {
        // Key was already activated - check if expired
        if ($expires_at <= time()) {
            $pdo->rollBack();
            http_response_code(410);
            die(json_encode(['success' => false, 'message' => 'Subscription key has expired']));
        }
        // Use remaining time from first activation
        $remaining_seconds = $expires_at - time();
        $expiry_date = date('Y-m-d H:i:s', time() + $remaining_seconds);
    }
    
    $stmt = $pdo->prepare("SELECT * FROM users WHERE hwid = ? AND id = ? LIMIT 1");
    $stmt->execute([$hwid, $telegram_chat_id]);
    $existing_user = $stmt->fetch(PDO::FETCH_ASSOC);
    
    if ($existing_user) {
        if ((bool)$existing_user['banned']) {
            $pdo->rollBack();
            http_response_code(403);
            die(json_encode(['success' => false, 'message' => 'User is banned']));
        }
        
        $current_subscription = strtotime($existing_user['subscription']);
        $new_subscription = strtotime($expiry_date);
        
        if ($current_subscription > time() && $new_subscription > $current_subscription) {
            $expiry_date = date('Y-m-d H:i:s', $current_subscription + ($days * 86400));
        }
        
        $stmt = $pdo->prepare("UPDATE users SET subscription = ?, last_key = ? WHERE hwid = ? AND id = ?");
        $stmt->execute([$expiry_date, $key, $hwid, $telegram_chat_id]);
    } else {
        $stmt = $pdo->prepare("SELECT COUNT(*) as hwid_count FROM users WHERE hwid = ?");
        $stmt->execute([$hwid]);
        $hwid_count = $stmt->fetch(PDO::FETCH_ASSOC)['hwid_count'];
        
        if ($hwid_count > 0) {
            $pdo->rollBack();
            http_response_code(409);
            die(json_encode(['success' => false, 'message' => 'HWID already registered with different account']));
        }
        
        $stmt = $pdo->prepare("INSERT INTO users (id, hwid, subscription, banned, last_key) VALUES (?, ?, ?, 0, ?)");
        $stmt->execute([$telegram_chat_id, $hwid, $expiry_date, $key]);
    }
    
    $stmt = $pdo->prepare("DELETE FROM subscriptions WHERE subscription_key = ?");
    $stmt->execute([$key]);
    
    $pdo->commit();
    
    // Send notification to the bot
    $notification_message = "✅ *Key Activated!*\n\n";
    $notification_message .= "🔑 *Key:* `{$key}`\n";
    $notification_message .= "💻 *HWID:* `{$hwid}`\n";
    $notification_message .= "📅 *Expires:* {$expiry_date}\n";
    $notification_message .= "👤 *User ID:* `{$telegram_chat_id}`\n";
    
    if ($existing_user) {
        $notification_message .= "\n📊 Subscription extended";
    } else {
        $notification_message .= "\n🎉 New subscription activated";
    }
    
    // Send notification asynchronously (do not block API response)
    send_telegram_message($telegram_chat_id, $notification_message, $bot_token);
    
    echo json_encode([
        'success' => true,
        'message' => $existing_user ? 'Subscription extended successfully' : 'Subscription activated successfully',
        'user' => [
            'id' => $telegram_chat_id,
            'hwid' => $hwid,
            'subscription' => $expiry_date,
            'banned' => false
        ]
    ]);
    
} catch(PDOException $e) {
    if (isset($pdo) && $pdo instanceof PDO && $pdo->inTransaction()) {
        $pdo->rollBack();
    }
    http_response_code(500);
    echo json_encode(['success' => false, 'message' => 'Database error']);
}
?>
<?php
declare(strict_types=1);

require __DIR__ . '/config.php';

header('Content-Type: application/json');

$hwid = trim($_REQUEST['hwid'] ?? '');
$key = trim($_REQUEST['key'] ?? '');

if ($hwid === '' || $key === '') {
    http_response_code(400);
    echo json_encode(['success' => false, 'message' => 'Missing required parameters: hwid and key']);
    exit;
}

if (strlen($hwid) > 255 || strlen($key) > 255) {
    http_response_code(400);
    echo json_encode(['success' => false, 'message' => 'Parameters too long']);
    exit;
}

function sendTelegramMessage(string $chatId, string $message, ?string $token): void
{
    if (empty($token)) {
        return;
    }

    $url = sprintf('https://api.telegram.org/bot%s/sendMessage', $token);
    $payload = http_build_query([
        'chat_id'    => $chatId,
        'text'       => $message,
        'parse_mode' => 'Markdown',
    ]);

    $ch = curl_init();
    curl_setopt_array($ch, [
        CURLOPT_URL => $url,
        CURLOPT_POST => true,
        CURLOPT_POSTFIELDS => $payload,
        CURLOPT_RETURNTRANSFER => true,
        CURLOPT_SSL_VERIFYPEER => false,
        CURLOPT_TIMEOUT => 3,
    ]);
    curl_exec($ch);
    curl_close($ch);
}

try {
    $pdo->beginTransaction();

    $stmt = $pdo->prepare("SELECT id, days, expires_at FROM subscriptions WHERE subscription_key = ? LIMIT 1");
    $stmt->execute([$key]);
    $subscription = $stmt->fetch();

    if (!$subscription) {
        $pdo->rollBack();
        http_response_code(404);
        echo json_encode(['success' => false, 'message' => 'Invalid subscription key']);
        exit;
    }

    $telegramChatId = (string)$subscription['id'];
    $days = (int)$subscription['days'];
    $expiresAt = !empty($subscription['expires_at']) ? strtotime((string)$subscription['expires_at']) : null;

    if ($days <= 0) {
        $pdo->rollBack();
        http_response_code(400);
        echo json_encode(['success' => false, 'message' => 'Invalid subscription duration']);
        exit;
    }

    // If key was never activated (expires_at is null), set it now (first activation)
    // If key was already activated, check if expired and use remaining time
    if ($expiresAt === null) {
        // First activation - set expires_at from now
        if ($days >= 99999) {
            $expiryDate = date('Y-m-d H:i:s', strtotime('+100 years'));
            $expiresAtTimestamp = strtotime('+100 years');
        } else {
            $expiryDate = date('Y-m-d H:i:s', strtotime("+{$days} days"));
            $expiresAtTimestamp = strtotime("+{$days} days");
        }
        // Update the key's expires_at in subscriptions table
        $updateStmt = $pdo->prepare("UPDATE subscriptions SET expires_at = ? WHERE subscription_key = ?");
        $updateStmt->execute([date('Y-m-d H:i:s', $expiresAtTimestamp), $key]);
    } else {
        // Key was already activated - check if expired
        if ($expiresAt <= time()) {
            $pdo->rollBack();
            http_response_code(410);
            echo json_encode(['success' => false, 'message' => 'Subscription key has expired']);
            exit;
        }
        // Use remaining time from first activation
        $remainingSeconds = $expiresAt - time();
        $expiryDate = date('Y-m-d H:i:s', time() + $remainingSeconds);
    }

    $stmt = $pdo->prepare("SELECT * FROM users WHERE hwid = ? AND id = ? LIMIT 1");
    $stmt->execute([$hwid, $telegramChatId]);
    $existingUser = $stmt->fetch();

    if ($existingUser) {
        if ((bool)$existingUser['banned']) {
            $pdo->rollBack();
            http_response_code(403);
            echo json_encode(['success' => false, 'message' => 'User is banned']);
            exit;
        }

        $currentSubscription = strtotime((string)$existingUser['subscription']);
        $newSubscription = strtotime($expiryDate);

        if ($currentSubscription > time() && $newSubscription > $currentSubscription) {
            $expiryDate = date('Y-m-d H:i:s', $currentSubscription + ($days * 86400));
        }

        $stmt = $pdo->prepare("UPDATE users SET subscription = ?, last_key = ? WHERE hwid = ? AND id = ?");
        $stmt->execute([$expiryDate, $key, $hwid, $telegramChatId]);
    } else {
        $stmt = $pdo->prepare("SELECT COUNT(*) AS hwid_count FROM users WHERE hwid = ?");
        $stmt->execute([$hwid]);
        $hwidCount = (int)$stmt->fetchColumn();

        if ($hwidCount > 0) {
            $pdo->rollBack();
            http_response_code(409);
            echo json_encode(['success' => false, 'message' => 'HWID already registered with different account']);
            exit;
        }

        $stmt = $pdo->prepare("INSERT INTO users (id, hwid, subscription, banned, last_key) VALUES (?, ?, ?, 0, ?)");
        $stmt->execute([$telegramChatId, $hwid, $expiryDate, $key]);
    }

    $stmt = $pdo->prepare("DELETE FROM subscriptions WHERE subscription_key = ?");
    $stmt->execute([$key]);

    $pdo->commit();

    $notification = "✅ *Key Activated!*\n\n"
        . "🔑 *Key:* `{$key}`\n"
        . "💻 *HWID:* `{$hwid}`\n"
        . "📅 *Expires:* {$expiryDate}\n"
        . "👤 *User ID:* `{$telegramChatId}`\n"
        . ($existingUser ? "\n📊 Subscription extended" : "\n🎉 New subscription activated");

    sendTelegramMessage($telegramChatId, $notification, $BOT_TOKEN);

    echo json_encode([
        'success' => true,
        'message' => $existingUser ? 'Subscription extended successfully' : 'Subscription activated successfully',
        'user' => [
            'id' => $telegramChatId,
            'hwid' => $hwid,
            'subscription' => $expiryDate,
            'banned' => false,
        ],
    ]);
} catch (PDOException $e) {
    if ($pdo->inTransaction()) {
        $pdo->rollBack();
    }
    http_response_code(500);
    echo json_encode(['success' => false, 'message' => 'Database error']);
}


-- Test equivalents of the statements in src/atlas_chat.cpp.
-- @token and @request are BINARY(16), i.e. Guid.ToByteArray(bigEndian: true).
-- The single @account fixture replaces the production bounded IN (numeric IDs).
-- Execute EXPIRE + CLAIM in one transaction, commit, then READ on any connection.

-- EXPIRE
UPDATE atlas_launcher_chat_outbox
SET status=4,last_error='expired',lease_token=NULL,lease_until=NULL
WHERE (realm_id=0 OR realm_id=@realm) AND status IN (0,1)
    AND expires_at<=UTC_TIMESTAMP(6) LIMIT 128;

-- CLAIM
UPDATE atlas_launcher_chat_outbox
SET status=1,realm_id=@realm,lease_token=@token,
    lease_until=DATE_ADD(UTC_TIMESTAMP(6),INTERVAL 30 SECOND),attempts=LEAST(attempts+1,65535)
WHERE (realm_id=@realm OR (realm_id=0 AND recipient_account_id IN (@account)))
    AND expires_at>UTC_TIMESTAMP(6)
    AND (status=0 OR (status=1 AND lease_until<UTC_TIMESTAMP(6)))
ORDER BY message_id LIMIT 64;

-- READ (the live Player/session/friend/created/expiry guards still run afterward)
SELECT o.message_id,m.sender_account_id,m.recipient_account_id,m.sender_username,m.body,
    IF(f.accepted_at IS NOT NULL,1,0) AS still_friends,
    TIMESTAMPDIFF(MICROSECOND,'1970-01-01 00:00:00',m.created_at) AS created_utc_micros,
    TIMESTAMPDIFF(MICROSECOND,'1970-01-01 00:00:00',o.expires_at) AS expires_utc_micros
FROM atlas_launcher_chat_outbox o
JOIN atlas_launcher_chat_message m ON m.id=o.message_id AND m.recipient_account_id=o.recipient_account_id
LEFT JOIN atlas_launcher_friendship f ON f.account_low_id=m.account_low_id AND f.account_high_id=m.account_high_id
WHERE o.realm_id=@realm AND o.status=1 AND o.lease_token=@token
    AND o.lease_until>UTC_TIMESTAMP(6)
ORDER BY o.message_id LIMIT 64;

-- ACK. @status=2 is delivered, 3 skipped, 4 failed; @error is NULL when delivered.
-- The production builder substitutes UTC_TIMESTAMP(6)/NULL and character/NULL.
UPDATE atlas_launcher_chat_outbox
SET status=@status,last_error=@error,
    delivered_at=CASE WHEN @status=2 THEN UTC_TIMESTAMP(6) ELSE NULL END,
    delivered_character_guid=@character,
    lease_token=NULL,lease_until=NULL
WHERE message_id=@message AND realm_id=@realm AND status=1 AND lease_token=@token;

-- INBOX. A retry keeps @request and never overwrites the original identity/body.
INSERT INTO atlas_launcher_chat_inbox
    (request_id,realm_id,sender_account_id,sender_character_guid,recipient_account_id,body,created_at)
VALUES (@request,@realm,@sender,@sender_character,@recipient,@body,UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE request_id=request_id;

-- RECEIPT. The production query has a bounded IN (up to 128 generated tokens).
SELECT LOWER(HEX(request_id)),status FROM atlas_launcher_chat_inbox
WHERE realm_id=@realm AND request_id IN (@request) AND status<>0 LIMIT 128;

//go:build e2e

package atlas_custom_test

import (
	"bytes"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/azerothcore/AzerothGhost/e2e/e2eharness"
	_ "github.com/go-sql-driver/mysql"
)

const fixture = "/opt/atlas-shop-tests/rename-20260911"
const evidence = "/opt/arthas-next/candidates/atlas-all-update-20260912/evidence"

func requireFixture(t *testing.T) {
	t.Helper()
	self, err := os.Readlink("/proc/self/ns/net")
	if err != nil {
		t.Fatal(err)
	}
	host, err := os.Readlink("/proc/1/ns/net")
	if err != nil || self == host {
		t.Fatal("a private fixture network is mandatory")
	}
	if os.Getenv("E2E_AUTH_ADDR") != "127.0.0.1:13724" {
		t.Fatal("unexpected fixture auth endpoint")
	}
	for key, database := range map[string]string{
		"E2E_AUTH_DSN": "shop_test_auth", "E2E_CHAR_DSN": "shop_test_chars", "E2E_WORLD_DSN": "shop_test_world",
	} {
		if !strings.Contains(os.Getenv(key), "@tcp(127.0.0.1:13308)/"+database) {
			t.Fatalf("unexpected fixture database for %s", key)
		}
	}
}

type reservedBot struct {
	GUID uint32 `json:"guid"`
	Name string `json:"name"`
}

func writeEvidence(t *testing.T, name string, value any) {
	t.Helper()
	payload, err := json.MarshalIndent(value, "", "  ")
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(evidence, name), append(payload, '\n'), 0600); err != nil {
		t.Fatal(err)
	}
}

// Phase one runs with random bot autologin disabled. The guild is created by
// actual GM commands; its synthetic leader and membership persist across the
// fixture restart so the startup reservation code has real data to consume.
func TestAtlas_PrepareGuildReservation(t *testing.T) {
	requireFixture(t)
	leader := e2eharness.NewSolo(t, e2eharness.ScenarioOpts{Prefix: "AtGld", Level: 80})
	rows, err := leader.CharDB.Query(`
		SELECT c.guid,c.name FROM shop_test_chars.characters c
		JOIN shop_test_auth.account a ON a.id=c.account
		JOIN shop_test_playerbots.playerbots_account_type p ON p.account_id=a.id
		LEFT JOIN shop_test_chars.guild_member g ON g.guid=c.guid
		WHERE a.username LIKE 'ATLASFIXTUREBOT%' AND p.account_type=1
		AND c.race IN (1,3,4,7,11) AND c.class<>6 AND g.guid IS NULL ORDER BY c.guid DESC LIMIT 2`)
	if err != nil {
		t.Fatal(err)
	}
	var bots []reservedBot
	for rows.Next() {
		var bot reservedBot
		if err := rows.Scan(&bot.GUID, &bot.Name); err != nil {
			t.Fatal(err)
		}
		bots = append(bots, bot)
	}
	if err := rows.Err(); err != nil {
		t.Fatal(err)
	}
	rows.Close()
	if len(bots) != 2 {
		t.Fatalf("expected two available synthetic Alliance random bots, found %d", len(bots))
	}
	guild := e2eharness.UniqueGuildName("AtlasFixture")
	leader.GM(t, fmt.Sprintf(".guild create %s %q", leader.Name, guild))
	for _, bot := range bots {
		leader.GM(t, fmt.Sprintf(".guild invite %s %q", bot.Name, guild))
	}
	var guildID, members uint32
	deadline := time.Now().Add(15 * time.Second)
	for time.Now().Before(deadline) {
		err = leader.CharDB.QueryRow(`SELECT g.guildid,COUNT(m.guid)
			FROM guild g JOIN guild_member m ON m.guildid=g.guildid
			WHERE g.leaderguid=? GROUP BY g.guildid`, leader.GUID).Scan(&guildID, &members)
		if err == nil && members == 3 {
			break
		}
		time.Sleep(100 * time.Millisecond)
	}
	if err != nil || members != 3 {
		t.Fatalf("guild was not created with its two bots: members=%d err=%v", members, err)
	}
	leader.Save(t)
	writeEvidence(t, "guild-reservation-input.json", map[string]any{
		"guildId": guildID, "guildName": guild, "leaderGuid": leader.GUID,
		"bots": bots, "preparedAt": time.Now().UTC(), "fixture": fixture,
	})
	t.Logf("PASS synthetic player-led guild %d has two bots reserved for the restart check", guildID)
}

// The issuing session must stay connected: Dungeon Clear deliberately aborts
// a run when its GM leaves. Follow the final record while the session is alive.
func TestAtlas_StartDungeonClear(t *testing.T) {
	requireFixture(t)
	issuer := e2eharness.NewSolo(t, e2eharness.ScenarioOpts{Prefix: "AtDc", Level: 80})
	started := time.Now().UTC()
	command := ".dc test start deadmines level=80 seed=20260912"
	issuer.GM(t, command)
	issuer.AssertWorldAlive(t)
	metadata := map[string]any{
		"command": command, "startedAt": started, "issuerGuid": issuer.GUID,
		"fixture": fixture, "completionPending": true,
	}
	writeEvidence(t, "dungeon-clear-start.json", metadata)
	deadline := started.Add(23 * time.Minute)
	for time.Now().Before(deadline) {
		payload, err := os.ReadFile(filepath.Join(fixture, "dc_testruns.jsonl"))
		if err != nil && !os.IsNotExist(err) {
			t.Fatal(err)
		}
		for _, line := range bytes.Split(payload, []byte{'\n'}) {
			var record struct {
				RunID        string            `json:"runId"`
				Dungeon      string            `json:"dungeon"`
				StartedAtMS  int64             `json:"startedAtMs"`
				CompSeed     uint32            `json:"compSeed"`
				Level        uint32            `json:"level"`
				Result       string            `json:"result"`
				FailReason   string            `json:"failReason"`
				BossesTotal  uint32            `json:"bossesTotal"`
				BossesKilled uint32            `json:"bossesKilled"`
				Comp         []json.RawMessage `json:"comp"`
			}
			if json.Unmarshal(line, &record) != nil || record.StartedAtMS < started.UnixMilli() ||
				record.Dungeon != "deadmines" || record.CompSeed != 20260912 || record.Level != 80 {
				continue
			}
			writeEvidence(t, "dungeon-clear-result.json", json.RawMessage(line))
			metadata["completionPending"] = false
			metadata["runId"] = record.RunID
			metadata["result"] = record.Result
			writeEvidence(t, "dungeon-clear-start.json", metadata)
			if record.Result != "success" || record.BossesTotal == 0 ||
				record.BossesKilled != record.BossesTotal || len(record.Comp) != 5 {
				t.Fatalf("Dungeon Clear ended: result=%s bosses=%d/%d party=%d reason=%s",
					record.Result, record.BossesKilled, record.BossesTotal, len(record.Comp), record.FailReason)
			}
			issuer.AssertWorldAlive(t)
			t.Logf("PASS complete autonomous run %s: %d/%d bosses, five bots", record.RunID,
				record.BossesKilled, record.BossesTotal)
			return
		}
		time.Sleep(time.Second)
	}
	t.Fatal("Dungeon Clear did not publish a matching final record before the fixture deadline")
}

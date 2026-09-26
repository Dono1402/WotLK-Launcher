#!/usr/bin/env python3
"""Preserve run identity while matching the server's whole-second timestamp."""
from prepare_candidate import ROOT

PRELUDE='''
	// The server reports whole seconds as milliseconds. Exclude every record
	// already present before the command, even one from this same second.
	logPath := filepath.Join(fixture, "dc_testruns.jsonl")
	seen := make(map[string]bool)
	prior, err := os.ReadFile(logPath)
	if err != nil && !os.IsNotExist(err) {
		t.Fatal(err)
	}
	for _, line := range bytes.Split(prior, []byte{'\\n'}) {
		var record struct { RunID string `json:"runId"` }
		if json.Unmarshal(line, &record) == nil && record.RunID != "" {
			seen[record.RunID] = true
		}
	}
'''

HELPER='''

func freshDungeonRecord(runID string, timestamp int64, started time.Time, seen map[string]bool) bool {
	return runID != "" && !seen[runID] && timestamp >= started.Truncate(time.Second).UnixMilli()
}

func TestAtlas_DungeonRecordFreshness(t *testing.T) {
	started := time.Unix(1790395276, 433119853)
	seen := map[string]bool{"old-same-second": true}
	cases := []struct {
		name string
		id string
		stamp int64
		want bool
	}{
		{"new record same second", "fresh", 1790395276000, true},
		{"earlier second", "stale", 1790395275000, false},
		{"preexisting same second", "old-same-second", 1790395276000, false},
		{"missing identity", "", 1790395276000, false},
		{"new later second", "later", 1790395277000, true},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if got := freshDungeonRecord(tc.id, tc.stamp, started, seen); got != tc.want {
				t.Fatalf("fresh record=%v want=%v", got, tc.want)
			}
		})
	}
}
'''

def adapt(text):
    if 'func freshDungeonRecord(' in text:
        return text
    replacements=[
        ('\tstarted := time.Now().UTC()\n\tcommand := ".dc test start deadmines',
         PRELUDE+'\tstarted := time.Now().UTC()\n\tcommand := ".dc test start deadmines'),
        ('os.ReadFile(filepath.Join(fixture, "dc_testruns.jsonl"))','os.ReadFile(logPath)'),
        ('record.StartedAtMS < started.UnixMilli()',
         '!freshDungeonRecord(record.RunID, record.StartedAtMS, started, seen)')]
    for old,new in replacements:
        if text.count(old)!=1:
            raise RuntimeError('Unexpected Dungeon Clear test source; do not relax the oracle.')
        text=text.replace(old,new)
    return text+HELPER

if __name__=='__main__':
    paths=[ROOT/'tests/atlas_custom_e2e_test.go',ROOT/'core/e2e/local/atlas/atlas_custom_e2e_test.go']
    originals=[p.read_text() for p in paths]
    if originals[0]!=originals[1]:
        raise RuntimeError('Runner source copies differ; inspect first.')
    updated=adapt(originals[0])
    for path in paths:
        path.write_text(updated)
    print('Adapted timestamp precision and added five pure freshness regression cases.')

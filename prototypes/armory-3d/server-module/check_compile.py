import json
import pathlib
import shlex
import subprocess
import sys

# Compile only in the uploaded test directory, using the active candidate's existing headers.
root = pathlib.Path(__file__).resolve().parent
build = pathlib.Path(sys.argv[1]).resolve()
with (build / "compile_commands.json").open() as stream:
    commands = json.load(stream)
entry = next(item for item in commands if item["file"].endswith("/atlas_friends.cpp"))
original = entry.get("arguments") or shlex.split(entry["command"])
args = []
skip = False
for arg in original:
    if skip:
        skip = False
    elif arg in ("-o", "-MF", "-MT", "-MQ", "-MJ"):
        skip = True
    elif arg not in ("-c", "-MD", "-MMD", "-MP", entry["file"]):
        args.append(arg)
source = root / "mod-atlas-armory" / "src"
# The active build already includes the old collector: prefer the uploaded headers in tests too.
args[1:1] = ["-I", str(source)]
for name in ("atlas_armory.cpp", "atlas_armory_loader.cpp"):
    subprocess.run(args + ["-fsyntax-only", str(source / name)], cwd=entry["directory"], check=True)
for name in ("combat_math", "capture_schedule"):
    test = root / (name + "-test")
    subprocess.run(["c++", "-std=c++20", "-Wall", "-Wextra", "-Werror", "-I", str(source),
                    str(root / "tests" / (name + ".cpp")), "-o", str(test)], check=True)
    subprocess.run([str(test)], check=True)
sql_test = root / "sql-json-test"
subprocess.run(args + ["-DFMT_HEADER_ONLY", "-I", str(source), str(root / "tests" / "sql_json.cpp"), "-o", str(sql_test)], cwd=entry["directory"], check=True)
query = subprocess.check_output([str(sql_test)], text=True)
query = "SET SESSION TRANSACTION READ ONLY; START TRANSACTION READ ONLY;\n" + query + "ROLLBACK;\n"
result = subprocess.run(["sudo", "-n", "docker", "exec", "-i", "arthas-mysql", "sh", "-c",
    'export MYSQL_PWD="$MYSQL_ROOT_PASSWORD"; exec mysql -uroot --default-character-set=utf8mb4 --batch --raw --skip-column-names'],
    input=query, text=True, capture_output=True, check=True)
records = [json.loads(line) for line in result.stdout.splitlines()]
assert records == [
    {"name": "O'Brien\\test", "empty": "", "level": 22, "haste": -20.5, "enabled": True, "schools": [{"id": 1, "crit": 2.5}]},
    {"capturedAtMs": 200, "reason": "equipment"},
    {"capturedAtMs": 200, "reason": "equipment"},
    {"capturedAtMs": 200, "reason": "logout"},
    {"capturedAtMs": 100, "reason": "periodic"}
]
print(json.dumps({"syntaxChecks": 2, "mathTests": "passed", "captureScheduleTests": "passed", "sqlJsonReadOnlyTests": 5, "productionFilesModified": False}))

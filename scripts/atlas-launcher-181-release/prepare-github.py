import hashlib,json,os,pathlib,pwd,shutil,subprocess
root=pathlib.Path('/opt/atlas-launcher-releases/1.8.1-20260926/client'); prepared=root/'prepared'; stage=pathlib.Path('/tmp/atlas-github-181-20260926')
assert not stage.exists(); stage.mkdir(mode=0o700)
def digest(p):
 with p.open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest()
files={'AtlasLauncherSetup.exe':root/'AtlasLauncherSetup.exe','armory-runtime.zip':prepared/'store/v1.8.1/armory-runtime.zip','launcher-update.json':prepared/'public/launcher-update.json','PATCH-NOTES.md':prepared/'store/v1.8.1/PATCH-NOTES.md','PATCH-NOTES.en.md':prepared/'store/v1.8.1/PATCH-NOTES.en.md'}
for name,source in files.items():shutil.copyfile(source,stage/name)
(stage/'SHA256SUMS.txt').write_text(''.join(digest(stage/name)+'  '+name+'\n' for name in files))
files['SHA256SUMS.txt']=stage/'SHA256SUMS.txt'
body=(stage/'PATCH-NOTES.md').read_text()+'\n## Installer ou mettre à jour\n\nTéléchargez **AtlasLauncherSetup.exe** pour installer Atlas Launcher. Si la mise à jour revient en 1.7.2 ou reste bloquée, fermez le launcher et installez cette version par-dessus : cette intervention est nécessaire une seule fois pour remplacer l’ancien programme de mise à jour. Les réglages sont conservés.\n\n[Download for Windows](https://github.com/Dono1402/WotLK-Launcher/releases/download/v1.8.1/AtlasLauncherSetup.exe) · [English release notes](https://github.com/Dono1402/WotLK-Launcher/releases/download/v1.8.1/PATCH-NOTES.en.md)\n\nLe paquet `armory-runtime.zip` et le manifeste sont destinés à la compilation et à la vérification ; ils ne sont pas nécessaires pour installer le launcher.\n'
(stage/'GITHUB-RELEASE.md').write_text(body)
inputs=json.loads((root/'inputs.json').read_text())
config=dict(commit=inputs['commit'],assets=[dict(name=name,bytes=(stage/name).stat().st_size,sha256=digest(stage/name)) for name in files])
(stage/'github-inputs.json').write_text(json.dumps(config,indent=2)+'\n')
shutil.copyfile('/tmp/atlas-release-181-20260926/publish-github.py',stage/'publish-github.py')
for name in ['release.json','patch-note.json','patch-note.en.json']:shutil.copyfile(prepared/'metadata/releases/v1.8.1'/name,stage/name)
user=pwd.getpwnam('debian')
for p in [stage,*stage.iterdir()]:os.chown(p,user.pw_uid,user.pw_gid)
subprocess.run(['runuser','-u','debian','--','python3',str(stage/'publish-github.py'),'--prepare'],check=True)
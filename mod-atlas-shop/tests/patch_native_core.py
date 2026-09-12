#!/usr/bin/env python3
"""Create an isolated source overlay from the pinned Atlas core; never edit it."""
import argparse
import hashlib
import json
from pathlib import Path

FILES = {
    'Entities/Player/PlayerStorage.cpp': 'bcd368071217a82e7a6af8ac001337fac0092beff22f6e143d3ece40ad379606',
    'Handlers/CharacterHandler.cpp': 'b1c36c230c457aa8bc139209103c8e9d2c59169cc2a550e71eee162356a963e0',
    'Server/WorldSession.cpp': '341ad4bec11dd5064882f07a9e5946ab41d2f9f4e3b20eda001f8a655471525c',
}


def once(text, old, new):
    if text.count(old) != 1:
        raise RuntimeError('Core anchor changed: ' + old[:120])
    return text.replace(old, new)


def patch(relative, source):
    marker = '#include "AccountMgr.h"'
    source = once(source, marker, marker + '\n#include "atlas_shop_native.h"')
    if relative.endswith('PlayerStorage.cpp'):
        source = once(source, '    _SaveCharacter(create, trans);',
                      '    AtlasShop::TrackSave(GetGUID().GetCounter(), trans, create);\n    _SaveCharacter(create, trans);')
        source += '\nuint32 AtlasShop::StorageHooksVersion() { return 2; }\n'
    elif relative.endswith('WorldSession.cpp'):
        source = once(source, 'WorldSession::~WorldSession()\n{',
                      'WorldSession::~WorldSession()\n{\n    AtlasShop::ForgetSession(this);')
        anchor = '    AntiDosOpcodePolicy const* policy = sWorldGlobals->GetAntiDosPolicyForOpcode(p.GetOpcode());'
        source = once(source, anchor,
                      '    // Account-service traffic has its own bounded budget. Ordinary enum packets\n'
                      '    // retain the existing three-per-second policy from the world database.\n'
                      '    if (AtlasShop::IsNativePacket(p))\n'
                      '        return AtlasShop::AllowNativePacket(Session) ? Policy::Process : Policy::DropPacket;\n\n' + anchor)
        source += '\nuint32 AtlasShop::SessionHooksVersion() { return 3; }\n'
    else:
        # Keep the request alive through all its asynchronous callbacks. Native
        # writes wait for both these requests and their later SQL transactions.
        for kind, var in [('Create', 'createInfo'), ('Rename', 'renameInfo'),
                          ('Customize', 'customizeInfo'), ('FactionChange', 'factionChangeInfo')]:
            anchor = f'    std::shared_ptr<Character{kind}Info> {var} = std::make_shared<Character{kind}Info>();'
            source = once(source, anchor, anchor + f'\n    AtlasShop::TrackNameWork({var});')
        anchor = '                        // Check name uniqueness in the same step as saving to database'
        source = once(source, anchor, '                        if (!AtlasShop::CharacterWritesAllowed(GetAccountId()))\n'
                      '                        {\n                            SendCharCreate(CHAR_CREATE_ERROR);\n                            return;\n                        }\n\n' + anchor)
        for method, info, response in [('HandleCharRenameCallBack', 'renameInfo', 'SendCharRename'),
                                      ('HandleCharCustomizeCallback', 'customizeInfo', 'SendCharCustomize'),
                                      ('HandleCharFactionOrRaceChangeCallback', 'factionChangeInfo', 'SendCharFactionChange')]:
            start = source.index('void WorldSession::' + method + '(')
            end = source.index('\nvoid WorldSession::', start + 1)
            body = source[start:end]
            body = once(body, '\n{\n    if (!result)', '\n{\n    if (!result || !AtlasShop::CharacterWritesAllowed(GetAccountId()))')
            if info == 'renameInfo':
                body = once(body, '    // Update name and at_login flag in the db',
                            '    CharacterDatabaseTransaction trans = CharacterDatabase.BeginTransaction();\n\n    // Update name and at_login flag in the db')
                if body.count('CharacterDatabase.Execute(stmt);') != 2:
                    raise RuntimeError('Unexpected legacy rename writes')
                body = body.replace('CharacterDatabase.Execute(stmt);', 'trans->Append(stmt);')
                anchor = '    LOG_INFO("entities.player.character",'
                body = once(body, anchor, '    AtlasShop::TrackSave(guidLow, trans, true);\n    CharacterDatabase.CommitTransaction(trans);\n\n' + anchor)
            else:
                body = once(body, '    CharacterDatabase.CommitTransaction(trans);',
                            '    AtlasShop::TrackSave(lowGuid, trans, true);\n    CharacterDatabase.CommitTransaction(trans);')
            source = source[:start] + body + source[end:]
        source += '\nuint32 AtlasShop::CharacterHooksVersion() { return 2; }\n'
    return source


def overlay(core, output):
    core = Path(core).resolve(strict=True)
    output = Path(output).resolve()
    if output == core or core in output.parents or output in core.parents:
        raise RuntimeError('The source overlay must be separate from the input core.')
    records = []
    for relative, expected in FILES.items():
        original = core / relative
        raw = original.read_bytes()
        if hashlib.sha256(raw).hexdigest() != expected:
            raise RuntimeError('Pinned core source differs: ' + relative)
        modified = patch(relative, raw.decode('utf-8'))
        target = output / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(modified, encoding='utf-8', newline='\n')
        records.append({'relative': relative, 'originalSha256': expected,
                        'overlaySha256': hashlib.sha256(target.read_bytes()).hexdigest()})
    return records


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--core', type=Path, required=True, help='The pinned src/server/game directory.')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(overlay(args.core, args.output), indent=2))

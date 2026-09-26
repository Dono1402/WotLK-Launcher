# Index exhaustif des commits amont — 26 septembre 2026

Cet index complète l'[explication en français](WOTLK-UPSTREAM-CHANGES-2026-09-26.md).
Il contient les **318 commits** absents des bases amont intégrées le 13 septembre,
fusions et imports de données compris. Les titres originaux restent en anglais
pour retrouver sans ambiguïté les sources ; ils ne sont pas des résultats de tests Atlas.
Les SHA figés correspondent à l'audit, pas nécessairement aux têtes futures des branches.

## core — 231 commits

Base : `06234df3d5ab26c93f4f1f06f3edb828b73ecd3c` ; tête : `7f12e89ee5f467a50e62eba1d525eac7dc953d03`.
[Comparaison complète](https://github.com/mod-playerbots/azerothcore-wotlk/compare/06234df3d5ab26c93f4f1f06f3edb828b73ecd3c...7f12e89ee5f467a50e62eba1d525eac7dc953d03).

- [b9cca47ef4](https://github.com/mod-playerbots/azerothcore-wotlk/commit/b9cca47ef4e9bae1fec7bebf609bd590835722fc) — fix(Core/Mail): correct the declared size of mail list entries (#27505)
- [42104bd7fb](https://github.com/mod-playerbots/azerothcore-wotlk/commit/42104bd7fb4d51c70d8ad39c73ca05e9338f3cb4) — fix(Core/Player): Fixed cross-zone weather residual bug (Burning Flatlands example). (#27116)
- [0125cfea79](https://github.com/mod-playerbots/azerothcore-wotlk/commit/0125cfea79179c9f2afc181a79778b590a59049f) — fix(Scripts/HoL): restore lieutenant follow (#27479)
- [4452f5371b](https://github.com/mod-playerbots/azerothcore-wotlk/commit/4452f5371b8eee80fe21e9bda3a73b824cf0768d) — fix(DB/SAI): make Trollgore invaders attack (#27478)
- [18c10cf0f7](https://github.com/mod-playerbots/azerothcore-wotlk/commit/18c10cf0f77074f30277d49738bab408aa700d0a) — chore(DB): import pending files
- [35ce56532b](https://github.com/mod-playerbots/azerothcore-wotlk/commit/35ce56532b5177c5367fc36ca25513363e67e725) — fix(DB/Quest): restrict Flatulate targets (#27477)
- [f21a0f43d4](https://github.com/mod-playerbots/azerothcore-wotlk/commit/f21a0f43d49c3d4e87c1f7439f216973a0d730b2) — chore(DB): import pending files
- [65c3bd1ae4](https://github.com/mod-playerbots/azerothcore-wotlk/commit/65c3bd1ae43b75bfbe1ed49379f0a7044580e519) — fix(Scripts/SSC): drive Hydross' form and beams from the Cleansing Field aura (#27503)
- [e1df8d15aa](https://github.com/mod-playerbots/azerothcore-wotlk/commit/e1df8d15aac39f93c983a15147f724937cbc5204) — chore(DB): import pending files
- [0d89b673b5](https://github.com/mod-playerbots/azerothcore-wotlk/commit/0d89b673b54f33b742aaabb06398b2835fc58d44) — fix(DB/Creature): Ironwork Cannon should attack players with Flame Cannon (#27401)
- [f7aa302dfc](https://github.com/mod-playerbots/azerothcore-wotlk/commit/f7aa302dfcf3a2d6917e180578bd426722c6efcc) — chore(DB): import pending files
- [a960ecb88c](https://github.com/mod-playerbots/azerothcore-wotlk/commit/a960ecb88cac69ce11982e8fee92adc0969e0c93) — fix(Core/Skills): use progressive crafting skill-up chance (#27219)
- [8c8ab800f4](https://github.com/mod-playerbots/azerothcore-wotlk/commit/8c8ab800f4c3331bdbe25fe4ef01ffb25b0d1af8) — fix(Scripts/Ulduar): correct Mimiron hardmode timer (#27480)
- [a5e625dfa3](https://github.com/mod-playerbots/azerothcore-wotlk/commit/a5e625dfa3d64061c45d8afb9f7b3a8dc523b5fa) — fix(DB/Quest): synchronize Platinum Discs visual aids (#27334)
- [0a0f850403](https://github.com/mod-playerbots/azerothcore-wotlk/commit/0a0f850403e304f6e6d4734b2d310bf40fd7a3ed) — chore(DB): import pending files
- [f854b77042](https://github.com/mod-playerbots/azerothcore-wotlk/commit/f854b77042f51a6522ad72b30149af4f960a3c14) — fix(Scripts/ZulAman): Zul'jin no longer stays in troll form when entering Dragonhawk phase (#27501)
- [b7658a339d](https://github.com/mod-playerbots/azerothcore-wotlk/commit/b7658a339d628a74a71b01aa8e59c4c2438d3093) — fix(Core/Mail): announce every delayed mail, not just the first (#27507)
- [85e8dcb283](https://github.com/mod-playerbots/azerothcore-wotlk/commit/85e8dcb283538155aceefcbc006ca51ae754d021) — fix(Scripts/Ulduar): Resolve Razorscale Breath issue. (#27409)
- [6233733d86](https://github.com/mod-playerbots/azerothcore-wotlk/commit/6233733d8632a4d55065cb4c1abbb697a454a2f9) — fix(Tests): build unit tests with core interface warnings (#26092)
- [0d4ddbfdc0](https://github.com/mod-playerbots/azerothcore-wotlk/commit/0d4ddbfdc09182e2cfc6020eecc51582c5e11800) — refactor(Core/Movement): Do a little cleanup for PointMovementGenerator.h (#27114)
- [54f1e6db2e](https://github.com/mod-playerbots/azerothcore-wotlk/commit/54f1e6db2e8cb8adedd8c67639a18b4126002284) — refactor(Core/Unit): extract rage weapon-speed term with test (#27004)
- [13cb5db8e7](https://github.com/mod-playerbots/azerothcore-wotlk/commit/13cb5db8e7953807d68459aaa29eb1a674b1916a) — fix(Scripts/Stratholme): Restore Scarlet Threads (#27342)
- [d072fd1588](https://github.com/mod-playerbots/azerothcore-wotlk/commit/d072fd1588685285e0ae323200bf15075d05cc4a) — chore(DB): import pending files
- [796f181cce](https://github.com/mod-playerbots/azerothcore-wotlk/commit/796f181cced14c5d84ce40360fc0eb0ff1d69630) — fix(Core/Script): target-aware MOVE\_FORWARD, spawn A Plague Upon Thee termites (#26725)
- [2d0b8818bc](https://github.com/mod-playerbots/azerothcore-wotlk/commit/2d0b8818bc93b6b9c12cc65e976cc1f303edb2bf) — feat(Core/Players): Add inventory arrival hook (#27018)
- [46aba01623](https://github.com/mod-playerbots/azerothcore-wotlk/commit/46aba01623792fbe2f1865b24a01f5fea099a32f) — fix(DB/Spells): prevent Frost Breath stun refresh (#27472)
- [011bb311c5](https://github.com/mod-playerbots/azerothcore-wotlk/commit/011bb311c5bab866a8ef66581be89c1fbede1c9c) — chore(DB): import pending files
- [cdfc61f799](https://github.com/mod-playerbots/azerothcore-wotlk/commit/cdfc61f7998cf52f2b782089b56201845fe7e944) — fix(DB/SAI): Correct Mobile Databank temple line offsets (#27511)
- [9d9b6049a3](https://github.com/mod-playerbots/azerothcore-wotlk/commit/9d9b6049a3ce38f31042899e1c6f4141dc526baa) — chore(DB): import pending files
- [3d21cd3e9d](https://github.com/mod-playerbots/azerothcore-wotlk/commit/3d21cd3e9d3a2340224f91cbb1d16dea61518581) — feat(Core/Scripting): add OnPlayerAfterTakeItemFromMail player hook (#27447)
- [9b484c10c9](https://github.com/mod-playerbots/azerothcore-wotlk/commit/9b484c10c9cbb8a7db31dccfab6b2d2295540ed2) — fix(DB/Loot): split Sapphiron 25 reference loot to pools (#26751)
- [bd16d75905](https://github.com/mod-playerbots/azerothcore-wotlk/commit/bd16d7590564c0aeaa153d7242e1a3a4fcf2fd0a) — chore(DB): import pending files
- [a827462238](https://github.com/mod-playerbots/azerothcore-wotlk/commit/a827462238e4df0fefcc8987285f10cb14b4ebbd) — fix(Core/Group): guard RemoveInvite against a stale group pointer (#27529)
- [18f28bd144](https://github.com/mod-playerbots/azerothcore-wotlk/commit/18f28bd1443adec3c922cbd8dbfc703ee8e9f5dd) — fix(Core/Spells): Prevent Grounding Totem from consuming AoE hits (#26809)
- [2b5ddea71d](https://github.com/mod-playerbots/azerothcore-wotlk/commit/2b5ddea71dbdb144bf49c05a1df5dc7e11b868f2) — fix(Core/Unit): cancel pending events before aura cleanup on map removal (#27527)
- [6b5b187439](https://github.com/mod-playerbots/azerothcore-wotlk/commit/6b5b187439c36cf9960b5b64105414eab5202cf9) — feat(Codestyle/SQL): require both id and guid in spawn DELETEs (#27517)
- [c974463f6d](https://github.com/mod-playerbots/azerothcore-wotlk/commit/c974463f6d26508c4dbe632cbfcf49258473bd26) — fix(DB/CreatureTemplate): allow pickpocketing on heroic difficulty entries (#26851)
- [c552679abc](https://github.com/mod-playerbots/azerothcore-wotlk/commit/c552679abc3d1658559b15b4c2da8802fbc3bff6) — chore(DB): import pending files
- [a5b478f448](https://github.com/mod-playerbots/azerothcore-wotlk/commit/a5b478f44862e1aeb69619f054529b19b874a879) — fix(Core/Creature): populate spawnId when saving creatures to DB (#27532)
- [bc9198ce71](https://github.com/mod-playerbots/azerothcore-wotlk/commit/bc9198ce71117163b7a7bb3f4d0e6ff5b6aa8c3a) — fix(Core/Pets): Prevent follow from interrupting channels (#27010)
- [ff11a34181](https://github.com/mod-playerbots/azerothcore-wotlk/commit/ff11a3418123ce96f2d94d44fa20da239e4c26c2) — fix(DB/Immunities): update Badlands Rock Elementals immunities (#27513)
- [b53c5576a9](https://github.com/mod-playerbots/azerothcore-wotlk/commit/b53c5576a93c329b11c71a2a5353723e93ca085b) — chore(DB): import pending files
- [c665359412](https://github.com/mod-playerbots/azerothcore-wotlk/commit/c66535941203544cc363f67f1dc45a81e8424039) — feat(Core/Arena): swap OnGetStartPersonalRating for OnAddMember, add OnArenaWeekReset (#27512)
- [885d4cc212](https://github.com/mod-playerbots/azerothcore-wotlk/commit/885d4cc21271fa5468908b210014d023d40edb9d) — fix(Core/Spells): General Vezax Shadow Crash puddles must not stack, strip their linked aura, or show a countdown (#27374)
- [9149945e50](https://github.com/mod-playerbots/azerothcore-wotlk/commit/9149945e50b43693449eef52d3068b94eb467892) — chore(DB): import pending files
- [34e96ea539](https://github.com/mod-playerbots/azerothcore-wotlk/commit/34e96ea539c7449064157394275bb115a29fefdc) — fix(DB/Immunities): Update Algalon Living Constellation immunities (#27541)
- [eb8d905a0d](https://github.com/mod-playerbots/azerothcore-wotlk/commit/eb8d905a0d0e95a784f4670734e5ebdddbbf982f) — fix(DB/Immunities): Update Algalon Dark Matter immunities (#27540)
- [eba11faa51](https://github.com/mod-playerbots/azerothcore-wotlk/commit/eba11faa515fec2a96cbab059c8b214788c9c51e) — fix(DB/Immunities): Update Ignis' Iron Constructs immunities (#27520)
- [927b2767b9](https://github.com/mod-playerbots/azerothcore-wotlk/commit/927b2767b941312824cb9e60785ecd9bd2b7da51) — chore(DB): import pending files
- [a1198491cb](https://github.com/mod-playerbots/azerothcore-wotlk/commit/a1198491cb5f22bdb0f6eab9daf096a050c83ffb) — fix(Core/Player): reject wrong-class relics in CanUseItem (#27530)
- [992d8c566e](https://github.com/mod-playerbots/azerothcore-wotlk/commit/992d8c566e77d5296e1dcfa14fa8e01736572c17) — fix(Core/Item): null-owner guard in CheckSoulboundTradeExpire (#27528)
- [fea7a17e0f](https://github.com/mod-playerbots/azerothcore-wotlk/commit/fea7a17e0f978c33a7adbc9d4304c4fe2dd92b98) — fix(Core/Pets): Pet follow and stay behavior when casting spells out of range (#26661)
- [d7ce67dc18](https://github.com/mod-playerbots/azerothcore-wotlk/commit/d7ce67dc1800f092bac98aad680ece1c201b89a0) — fix(Scripts/Naxxramas): Gluth - zombie chow locations (#27074)
- [e89548f2f7](https://github.com/mod-playerbots/azerothcore-wotlk/commit/e89548f2f717ca7615bb521a9ecb0422e97ad215) — fix(Core/Spells): respect hostile self-immunity (#27482)
- [1702c28b74](https://github.com/mod-playerbots/azerothcore-wotlk/commit/1702c28b748c7bcbd66863dadd8767f151805a51) — fix(Scripts/Ulduar): Spawn Thorim's golem hand bunnies statically and drop their combat (#27523)
- [c80c4b9792](https://github.com/mod-playerbots/azerothcore-wotlk/commit/c80c4b97921c2ad57dc2721de504d4f701b54906) — chore(DB): import pending files
- [a5e0e6b8f2](https://github.com/mod-playerbots/azerothcore-wotlk/commit/a5e0e6b8f2bf878cb45cb1dc2251eb1448b9bbc3) — fix(Scripts/Ulduar): constellations closing black holes while in the air (#27543)
- [3acb397bed](https://github.com/mod-playerbots/azerothcore-wotlk/commit/3acb397bed4026ef053b8e66ee55549d22bee01f) — fix(DB/Creature): restore Forest Swarmer patrol (#27475)
- [9ac116daba](https://github.com/mod-playerbots/azerothcore-wotlk/commit/9ac116dabac28dca52b38cf7322605ffbe8cf55d) — fix(DB/Creature): link Storm Tempered Keeper pairs (#27493)
- [2bed82dc92](https://github.com/mod-playerbots/azerothcore-wotlk/commit/2bed82dc92e8955dfce56a49238ff46ceebdf0c9) — chore(DB): import pending files
- [7d0367957c](https://github.com/mod-playerbots/azerothcore-wotlk/commit/7d0367957cd8493e870f6fc0a4f223ceae85694e) — fix(DB/Gameobject): Correct Call to Arms banner sides in Shattrath and Dalaran (#27559)
- [faf25cdfa8](https://github.com/mod-playerbots/azerothcore-wotlk/commit/faf25cdfa8a5bf0d8bfd46662d1fd5620ca83bdf) — chore(DB): import pending files
- [08627ec94c](https://github.com/mod-playerbots/azerothcore-wotlk/commit/08627ec94cccee4566c91b1ed093f1bd16c603a4) — fix(Core/Unit): Use caster's level to calculate spell resistance (#27558)
- [61efa7d012](https://github.com/mod-playerbots/azerothcore-wotlk/commit/61efa7d012dfba2f80c4bb61435fd409cd1b9650) — fix(DB/Quest): Remove pre-quest requirement for Amani Encroachment (#27542)
- [45e6512e47](https://github.com/mod-playerbots/azerothcore-wotlk/commit/45e6512e47d9b54370ec325347083534d13f50d6) — chore(DB): import pending files
- [847a896089](https://github.com/mod-playerbots/azerothcore-wotlk/commit/847a896089296dd34d2eb1fdb9115113c2ba7055) — fix(DB/Loot): remove Grunnda vendor drops (#27474)
- [646bdb706a](https://github.com/mod-playerbots/azerothcore-wotlk/commit/646bdb706ad5ba04f779ad481f383460e2c78c73) — chore(DB): import pending files
- [065be4cc88](https://github.com/mod-playerbots/azerothcore-wotlk/commit/065be4cc88a409da0c0c92302d5760bacf3939b4) — fix(DB/Quest): Gate The Drakkari Do Not Need Water Elementals! (#27471)
- [db533ad753](https://github.com/mod-playerbots/azerothcore-wotlk/commit/db533ad7537a0641d076b13e5611f29f33558d06) — chore(DB): import pending files
- [b0796ebda4](https://github.com/mod-playerbots/azerothcore-wotlk/commit/b0796ebda4a9d913de08fe21ddba10d9c36bcc9a) — fix(DB/SAI): Correct Frostmourne Cavern text and RP progression(#27522)
- [e00b2a7fd0](https://github.com/mod-playerbots/azerothcore-wotlk/commit/e00b2a7fd0a034693407236797ae96029465f83e) — chore(DB): import pending files
- [45d3871c24](https://github.com/mod-playerbots/azerothcore-wotlk/commit/45d3871c24f2b471ab55c1932f150c356050ca37) — fix(DB/Quest): gate Relics of the Snow Leopard Goddess behind Breaking Through Jin'Alai (#27458)
- [41f23d1148](https://github.com/mod-playerbots/azerothcore-wotlk/commit/41f23d11484a66c6f175e92eba30b02578787f10) — chore(DB): import pending files
- [97f3efdab5](https://github.com/mod-playerbots/azerothcore-wotlk/commit/97f3efdab5514ef1c46cac02ca6c34d87f717634) — Fix(DB/AreaTrigger): Gnomeregan AreaTrigger teleport location (#27459)
- [1668534311](https://github.com/mod-playerbots/azerothcore-wotlk/commit/16685343110115b12e76d517f7bac15c6a97fb2a) — chore(DB): import pending files
- [301401c9da](https://github.com/mod-playerbots/azerothcore-wotlk/commit/301401c9da2626f6155de92d969bf2eb26086a77) — feat(Core/Scripting): add player hooks for trainers and spell learning (#27552)
- [19d398159a](https://github.com/mod-playerbots/azerothcore-wotlk/commit/19d398159ab2db4229c2459ad94a2125ba1931a2) — fix(Scripts/Ulduar): fix Mimiron's Laser Barrage visual rotation and wipe desync (#27555)
- [01a025a151](https://github.com/mod-playerbots/azerothcore-wotlk/commit/01a025a1513e4cb658a9fd457575ec98ae0c6bb9) — fix(Scripts/Ulduar): Prevent Expedition Base Camp protective bubble from respawning after gauntlet start (#27442)
- [777bff578c](https://github.com/mod-playerbots/azerothcore-wotlk/commit/777bff578c5a7e9dca5fcff00ac5215243b2d198) — chore(DB): import pending files
- [083efa2bd7](https://github.com/mod-playerbots/azerothcore-wotlk/commit/083efa2bd75529b481d135d52fbf54da392ccfbb) — fix(DB/Loot): Correct Steelbreaker 10-man hard mode loot (#27566)
- [d0ca81bdea](https://github.com/mod-playerbots/azerothcore-wotlk/commit/d0ca81bdea70b41cdc155d4e599a22c854c4bba2) — docs(agents): warn against summoning entries tracked by InstanceScript ObjectData (#27565)
- [533260d203](https://github.com/mod-playerbots/azerothcore-wotlk/commit/533260d203c5cb49ec5e4a490e90bea2255de6bb) — chore(DB): import pending files
- [75ca855930](https://github.com/mod-playerbots/azerothcore-wotlk/commit/75ca8559306a4fb3dc168be507efdd05a0a99036) — fix(Scripts/VaultOfArchavon): Respawn Emalon's minions at the boss and announce them (#27433)
- [af37e62b8f](https://github.com/mod-playerbots/azerothcore-wotlk/commit/af37e62b8fcccbf4e1f3aa893dbfe455ceb9dc2b) — fix(DB/SAI): correct Serpentbloom Snake movement (#27467)
- [405adb5426](https://github.com/mod-playerbots/azerothcore-wotlk/commit/405adb542651157bf2e0db9656b10e8dc88c8d77) — chore(DB): import pending files
- [b19162f9f9](https://github.com/mod-playerbots/azerothcore-wotlk/commit/b19162f9f9cb6ed37d2cf16d67d64cc77d66f056) — fix(Scripts/Dragonblight): make Alystros' Lapsing Dream affect players (#27356)
- [862a6615e6](https://github.com/mod-playerbots/azerothcore-wotlk/commit/862a6615e67e85196c36009018fff291fe396d61) — chore(DB): import pending files
- [19ca6e1128](https://github.com/mod-playerbots/azerothcore-wotlk/commit/19ca6e11284d5bd2a2c274b6e88906083504ee14) — test(e2e): review, flakes, no unsolicited e2e (#27514)
- [3cdc64fea6](https://github.com/mod-playerbots/azerothcore-wotlk/commit/3cdc64fea6133298d4d93b95ad706c6b98a42043) — fix(DB/Loot): Allow Kirtonos the Herald to drop greens as well as at least two equipment (#27377)
- [501dd458b6](https://github.com/mod-playerbots/azerothcore-wotlk/commit/501dd458b66267e78264874ae3b9f8b47f5d4be1) — chore(DB): import pending files
- [516af2d687](https://github.com/mod-playerbots/azerothcore-wotlk/commit/516af2d687489b5ac71d7686c35d792cd823c119) — fix(DB/Quest): Show objective marker on Zaxxis mobs (#27569)
- [23e831771f](https://github.com/mod-playerbots/azerothcore-wotlk/commit/23e831771f5f82e90a261ff3e32488ae3ae2d90d) — chore(DB): import pending files
- [9183925666](https://github.com/mod-playerbots/azerothcore-wotlk/commit/9183925666cfa010a851bd6411576e20908d8f5e) — fix(Scripts/Ulduar): Use Thorim Sif's 10-man spell ids (#27575)
- [2c94fbfbf6](https://github.com/mod-playerbots/azerothcore-wotlk/commit/2c94fbfbf6e3dd95a1e4a1b8c198d6f391d5482d) — fix(Scripts/Ulduar): Respawn Hodir's Rare Cache with the boss (#27582)
- [3771844e4c](https://github.com/mod-playerbots/azerothcore-wotlk/commit/3771844e4c49ad7c484219d4c955928f09a91869) — chore(DB): import pending files
- [5577f4003c](https://github.com/mod-playerbots/azerothcore-wotlk/commit/5577f4003c2405ffa1f5606df7edd2d8910a5fc9) — fix(DB/Loot): Remove Petrified Shinbone from random lists (#27588)
- [61a55e194e](https://github.com/mod-playerbots/azerothcore-wotlk/commit/61a55e194ee62c4687719fb9f77226f2273054e7) — chore(DB): import pending files
- [02926c2bda](https://github.com/mod-playerbots/azerothcore-wotlk/commit/02926c2bda6f7b7ff37c451815a97dbf780208a4) — fix(Scripts/Ulduar): stop XT-002 summoning adds after death (#27589)
- [76bc632dc0](https://github.com/mod-playerbots/azerothcore-wotlk/commit/76bc632dc03d17fb0996934a8afbbba09bf5ac58) — fix(DB/Creature): sync Freya's 25-man HARD\_RESET flag (#27587)
- [251368adb3](https://github.com/mod-playerbots/azerothcore-wotlk/commit/251368adb36c86738096a5845605632dd05da2bd) — chore(DB): import pending files
- [f4763cc9eb](https://github.com/mod-playerbots/azerothcore-wotlk/commit/f4763cc9ebdd30b20d1a7b98c62c941dec7a0c48) — fix(Scripts/Ulduar): give Flame Leviathan's Freya's Ward lashers their own targeting AI (#27567)
- [b3e01ee2ca](https://github.com/mod-playerbots/azerothcore-wotlk/commit/b3e01ee2caf2d842e16dd85fd5ca617a973bd08b) — chore(DB): import pending files
- [4ddcea8503](https://github.com/mod-playerbots/azerothcore-wotlk/commit/4ddcea85032c490e764099e93552d8942dae5cb2) — fix(DB/Creature): stop Mimiron DB Target entering combat (#27594)
- [2fc1abea29](https://github.com/mod-playerbots/azerothcore-wotlk/commit/2fc1abea29e2d6ed7579c1a616f147bc29b9563a) — chore(DB): import pending files
- [ff8ed0c7f2](https://github.com/mod-playerbots/azerothcore-wotlk/commit/ff8ed0c7f2c29892f2dd7447168edb959623a7d9) — fix(DB/Ulduar): Void Zone summoned during XT-002 Deconstructor Hard Mode doen't stay in combat with the players. (#27596)
- [b4a7c2ddc0](https://github.com/mod-playerbots/azerothcore-wotlk/commit/b4a7c2ddc0c5b8841bb5ca048a49f1b100a09995) — chore(DB): import pending files
- [c181b77d3c](https://github.com/mod-playerbots/azerothcore-wotlk/commit/c181b77d3c2b0c34e2a9489a6bb05e8b7adbd57f) — fix(DB/SAI): Attempt another fix for Sky Darkener accumulation (#27597)
- [c9a5efc491](https://github.com/mod-playerbots/azerothcore-wotlk/commit/c9a5efc491fae58eaa3c3856512b59cb92d2d096) — chore(DB): import pending files
- [b34efa2abb](https://github.com/mod-playerbots/azerothcore-wotlk/commit/b34efa2abbde6bb270ccece687fd1520f88a65a7) — docs(agents): set flags\_extra on every difficulty entry (#27586)
- [65d564c848](https://github.com/mod-playerbots/azerothcore-wotlk/commit/65d564c8487459fe1cc3fcbde061a9a111f13e34) — fix(Core/Creature): Keep a spell's own recovery time on category cooldowns (#27595)
- [4cc1210a11](https://github.com/mod-playerbots/azerothcore-wotlk/commit/4cc1210a110f26a757fd3135bd3bad9789c4c226) — fix(Scripts/IcecrownCitadel): restore flight animations for Sindragosa, Rimefang, Spinestalker and Spire Frostwyrm (#26738)
- [d0825055c9](https://github.com/mod-playerbots/azerothcore-wotlk/commit/d0825055c9221e63917bab5cd7380a007ac40a54) — docs(agents): state the PR title convention inline (#27568)
- [dbacb5bb1b](https://github.com/mod-playerbots/azerothcore-wotlk/commit/dbacb5bb1be17a4ea2329ad82a5540135961c444) — fix(Scripts/Ulduar): keep Kologarn's corpse as the bridge across instance reloads (#27557)
- [527e91b9b2](https://github.com/mod-playerbots/azerothcore-wotlk/commit/527e91b9b2b0ae97c6c7b6f1374194b4f4a3b0ff) — fix(Core/Chat): accept uppercase link colors and the 'found' tag (#27580)
- [c1893c0e51](https://github.com/mod-playerbots/azerothcore-wotlk/commit/c1893c0e512af95e337bf8e8b44e17831ca3218d) — fix(Core/Database): allow reopening a DatabaseWorkerPool after a failed Open() (#27585)
- [90bccf4fa6](https://github.com/mod-playerbots/azerothcore-wotlk/commit/90bccf4fa6be193093185702f9cb5849ddba4827) — fix(Shared/DBC): keep DBC string when DB override column is empty (#27581)
- [39ffb837d5](https://github.com/mod-playerbots/azerothcore-wotlk/commit/39ffb837d5d169490c9a09eba466e481fc459e5a) — feat(Core/Deps): require Boost.Thread alongside the other Boost components (#27584)
- [9b7a65535b](https://github.com/mod-playerbots/azerothcore-wotlk/commit/9b7a65535b7242f55770e0f0f4e80e9ff96dd479) — fix(Core/Item): unlock openable item on the client when opened while dead (#27431)
- [b0bb3ae8bd](https://github.com/mod-playerbots/azerothcore-wotlk/commit/b0bb3ae8bdfd313583b27698877469fa74d5dceb) — fix(DB/Ulduar): award Stokin' the Furnace on Ignis kill (#27598)
- [985c747291](https://github.com/mod-playerbots/azerothcore-wotlk/commit/985c7472916c9484dd590be3ed8f2850074ce8ad) — chore(DB): import pending files
- [875358dc2a](https://github.com/mod-playerbots/azerothcore-wotlk/commit/875358dc2a730e03e3e9aa65503103ea24bad105) — fix(Scripts/Ulduar): keep Razorscale grounded during her permanent ground phase (#27610)
- [bd0c624eb2](https://github.com/mod-playerbots/azerothcore-wotlk/commit/bd0c624eb2a3757a890792014e3b448b7f46d3c2) — chore(DB): import pending files
- [479848ec5d](https://github.com/mod-playerbots/azerothcore-wotlk/commit/479848ec5d44727926fce14886d5a4615b748016) — fix(Scripts/MagtheridonsLair): anchor the release countdown to the Channeler pull (#27600)
- [968cf32814](https://github.com/mod-playerbots/azerothcore-wotlk/commit/968cf3281481e5bfb9357741ffa1d8ac84c9d964) — fix(DB/SAI): drop Mogg's link to an undefined event (#27609)
- [3ef3432cb6](https://github.com/mod-playerbots/azerothcore-wotlk/commit/3ef3432cb6cde0f7faf3eac2839a215e07061dd8) — chore(DB): import pending files
- [c0e6272af9](https://github.com/mod-playerbots/azerothcore-wotlk/commit/c0e6272af9a1a95aa0859a0cd95f7b70b273c9f4) — fix(Scripts/Ulduar): make Yogg-Saron's Laughing Skulls respect line of sight (#27614)
- [ab951fb735](https://github.com/mod-playerbots/azerothcore-wotlk/commit/ab951fb73531e39fa2c80af93b1f622f8fc7f6a2) — fix(DB/Scholomance): guarantee one rare loot from Lord Alexei Barov (#27270)
- [01c58df5bd](https://github.com/mod-playerbots/azerothcore-wotlk/commit/01c58df5bda7c38157f3aec67673aa2456d57286) — chore(DB): import pending files
- [553012d86f](https://github.com/mod-playerbots/azerothcore-wotlk/commit/553012d86fbf0b55071247550b22c9810c1e3a02) — fix(Core/Spells): load all spell\_custom\_attr rows (#27365)
- [74487848ea](https://github.com/mod-playerbots/azerothcore-wotlk/commit/74487848ea89f74f2caac115c912a08e42b29376) — fix(Core/Pets): Stop moving for autocast channels (#27564)
- [8cd69f96d6](https://github.com/mod-playerbots/azerothcore-wotlk/commit/8cd69f96d6808098a4c7ee8821083c96355f2a37) — Merge fix *(fusion)*
- [6e4cb47547](https://github.com/mod-playerbots/azerothcore-wotlk/commit/6e4cb47547515f9c67f7315389a71d1b5b2c168b) — - Fixed return in CheckSoulboundTradeExpire
- [f0f08f1331](https://github.com/mod-playerbots/azerothcore-wotlk/commit/f0f08f1331e74226b1742baf9bc290efdd18a8a9) — fix(Scripts/Ulduar): cast Razorscale's Wing Buffet once per grounding (#27616)
- [708403602a](https://github.com/mod-playerbots/azerothcore-wotlk/commit/708403602a9c18499990a3316ca935ec0dc8f912) — fix(Core/Spells): Prevent taunts from affecting totems (#27601)
- [c3e3d216bf](https://github.com/mod-playerbots/azerothcore-wotlk/commit/c3e3d216bf9a991895354248e9267ed455d870e9) — fix(Core/Spells): allow paladin auras from different casters to coexist (#27509)
- [83ad15d755](https://github.com/mod-playerbots/azerothcore-wotlk/commit/83ad15d75507fc962dc55a85c07ae32e7ae1c64c) — fix(DB/Quest): Summon Lord Kragaru from the Serpent Statue (#27611)
- [09c70f28a2](https://github.com/mod-playerbots/azerothcore-wotlk/commit/09c70f28a2c7c220a804e4a349bd8c21db5ab4e8) — fix(Scripts/Ulduar): stop Flame Leviathan saving the raid on pull (#27622)
- [ae7ed2f096](https://github.com/mod-playerbots/azerothcore-wotlk/commit/ae7ed2f0964b6fe2832f2fab8f7ab76991de6e16) — chore(DB): import pending files
- [186dfc7fa1](https://github.com/mod-playerbots/azerothcore-wotlk/commit/186dfc7fa152f017272d2c622e918d8f6f375e27) — fix(DB/Ulduar): correct 10-man and 25-man loot pools (#27624)
- [4a91be84ab](https://github.com/mod-playerbots/azerothcore-wotlk/commit/4a91be84ab18a51b2e4f50e6b3df3c7aed8fac13) — chore(DB): import pending files
- [5054796f66](https://github.com/mod-playerbots/azerothcore-wotlk/commit/5054796f66bc49960e1ec93e02dfa365e61b484a) — fix(Core/Spells): stop forced casts from inheriting the caster as their destination (#27621)
- [b6f62f5228](https://github.com/mod-playerbots/azerothcore-wotlk/commit/b6f62f522828a1f3dbcc19781f971c2d1e43d4b3) — fix(DB/Creature): set NO\_PARRY\_HASTEN for t7-t10 bosses (#27625)
- [8516fd49a6](https://github.com/mod-playerbots/azerothcore-wotlk/commit/8516fd49a6d1f89e4552d877594dfbaf9d548ebe) — chore(DB): import pending files
- [568ea12bbe](https://github.com/mod-playerbots/azerothcore-wotlk/commit/568ea12bbee6e5a88a7427bcc822ce89b04307bb) — fix(Core/Movement): Prevent waypoints from passing through the ground when using SmoothTransition (#27107)
- [e826d9f6dc](https://github.com/mod-playerbots/azerothcore-wotlk/commit/e826d9f6dc065f526ffefc7f55a9d6d2303ebe68) — fix(Core/Pets): Resume chase after deferred casts (#27623)
- [275ce1e609](https://github.com/mod-playerbots/azerothcore-wotlk/commit/275ce1e609801282f656ecd28a92cc1db6821569) — fix(Scripts/Silverpine): prevent duplicate Pyrewood Ambush events (#26832)
- [f7759071a0](https://github.com/mod-playerbots/azerothcore-wotlk/commit/f7759071a0e7f1ab4fd43f029641652e03149510) — fix(Scripts/SlavePens): Fixed the loot permissions for boss\_ahune's chest in Slave Pens (#27117)
- [a20beb0a80](https://github.com/mod-playerbots/azerothcore-wotlk/commit/a20beb0a80e9339dbddb23434cc14a9f03828c81) — feat(Core/Scripting): Add spell aura exclusivity hook (#26565)
- [d93aa1eb30](https://github.com/mod-playerbots/azerothcore-wotlk/commit/d93aa1eb301f7e4de85ac8d22d24c1e422879bca) — fix(Core/Vehicle): avoid Vehicle-kit use-after-free in TeleportVehicle (#26116)
- [e9675bf3e0](https://github.com/mod-playerbots/azerothcore-wotlk/commit/e9675bf3e0ab3016c9da9f8fb65b9ab47f75d033) — fix(Scripts/Ulduar): give Elder Brightleaf's Unstable Sun Beams their own lifetime (#27619)
- [6447b1643c](https://github.com/mod-playerbots/azerothcore-wotlk/commit/6447b1643c7bdad2136fc13f1af12bf1d7b058d1) — chore(DB): import pending files
- [f1bef3bc0a](https://github.com/mod-playerbots/azerothcore-wotlk/commit/f1bef3bc0a2f6396175e184c2cac70df77b46d11) — fix(Core/Movement): Fix pet pathing and chase angle against oversized targets (e.g. Brain of Yogg-Saron) (#27426)
- [e39b3f6f28](https://github.com/mod-playerbots/azerothcore-wotlk/commit/e39b3f6f28a8bd611e1265cd7001aa5d227ee9a2) — fix(Core/Unit): clear moving flags before sending root (#27563)
- [e6cca5d733](https://github.com/mod-playerbots/azerothcore-wotlk/commit/e6cca5d73395f29c15b1653b5fbad74e0fd8b170) — Merge pull request #248 from kadeshar/20260912-test-staging *(fusion)*
- [7409484aa3](https://github.com/mod-playerbots/azerothcore-wotlk/commit/7409484aa365d790aa963f7de19f3d4fa21c92d2) — fix(Scripts/Ulduar): stop Yogg-Saron's Psychosis and Malady of the Mind from picking low Sanity targets (#27628)
- [4a704e8190](https://github.com/mod-playerbots/azerothcore-wotlk/commit/4a704e81908c577ebaa2811d416afc57656b2795) — - Fix for PointMovementGenerator constructor
- [67317e09df](https://github.com/mod-playerbots/azerothcore-wotlk/commit/67317e09df8640de582c80d7ce206a92d8fb4a7b) — fix(Core): Correct Spelling of WorldSession::isLogingOut() & Use PascalCase (#27238)
- [cd8dc5775b](https://github.com/mod-playerbots/azerothcore-wotlk/commit/cd8dc5775b4ed09b15f622a38e5326c9583736c3) — feat(Core/Mail): add Mail.PushInboxOnDelivery to refresh stale mailboxes (#27508)
- [fe69136688](https://github.com/mod-playerbots/azerothcore-wotlk/commit/fe6913668821601b07e13366716db2e9dfb1701d) — refactor(Core/Spells): allow any WorldObject to be a spell caster (#27627)
- [880839d8c6](https://github.com/mod-playerbots/azerothcore-wotlk/commit/880839d8c6297526039f4e6a73d59cde5ecda8b9) — Merge pull request #250 from kadeshar/20260912-test-staging *(fusion)*
- [9c416aaacb](https://github.com/mod-playerbots/azerothcore-wotlk/commit/9c416aaacb5537636abb13c80f55a88947838e33) — fix(Core/Groups): finalize timed-out loot votes (#27486)
- [71012e3ce3](https://github.com/mod-playerbots/azerothcore-wotlk/commit/71012e3ce3e4a276b3fe9ded20481187c5daceb1) — fix(Core/Groups): identify the finished roll in SMSG\_LOOT\_ROLL\_WON (#27631)
- [46d4e0a39a](https://github.com/mod-playerbots/azerothcore-wotlk/commit/46d4e0a39a30287f133885a5a924b66bf44186c1) — feat(Core/World): apply PlayerLimit on config reload (#27633)
- [92f5ce9a73](https://github.com/mod-playerbots/azerothcore-wotlk/commit/92f5ce9a7351c0abdeba485148275b3e8fffc86f) — fix(Scripts/Ulduar): give Algalon a stasis period after Big Bang (#27630)
- [cc280cd188](https://github.com/mod-playerbots/azerothcore-wotlk/commit/cc280cd1886fabc5944385ca6a0ae2bef753db49) — fix(Scripts/Spells): Glyph of Lightwell not increasing Lightwell healing (#27162)
- [7907c668b1](https://github.com/mod-playerbots/azerothcore-wotlk/commit/7907c668b1b349f489006459c3cc90a1e30696e9) — fix(DB/Ulduar): give Freya's Elders their emblems (#27634)
- [e962278b9b](https://github.com/mod-playerbots/azerothcore-wotlk/commit/e962278b9b4fdeb0f39d007a5f89230c854c6fe1) — chore(DB): import pending files
- [e1823bb2db](https://github.com/mod-playerbots/azerothcore-wotlk/commit/e1823bb2db751a7cc0a90a8543e778449ebf7d84) — feat(Core/Database): enable modules to own their database (#27544)
- [d32c4abe8b](https://github.com/mod-playerbots/azerothcore-wotlk/commit/d32c4abe8b9093ccc43d8c27d1456564b1629903) — fix(Scripts/Ulduar): cap Elder Brightleaf's Unstable Sun Beams (#27638)
- [7970acf716](https://github.com/mod-playerbots/azerothcore-wotlk/commit/7970acf716c51ad235dc2388e9308c2ee0984cfb) — chore(DB): import pending files
- [a0019cea78](https://github.com/mod-playerbots/azerothcore-wotlk/commit/a0019cea78d96c77905fe1bea324f8ba8aac1c9c) — fix(Core/Events): do not count a holiday as active during its building stage (#27646)
- [0327e81994](https://github.com/mod-playerbots/azerothcore-wotlk/commit/0327e819944de4a9202dee0aac977aefbdaa1814) — fix(Core/Spells): preserve loaded aura amount without caster (#27375)
- [658448f69a](https://github.com/mod-playerbots/azerothcore-wotlk/commit/658448f69a71dfd77500cf277cb33fff70a1bb8d) — fix(Core/Spell): Don't put ritual spells on cooldown when the spell is cancelled. (#27643)
- [8362ae1635](https://github.com/mod-playerbots/azerothcore-wotlk/commit/8362ae1635508d116add7c95528561c7bd79068c) — fix(Core/Battlefield): resurrect ghosts when a Wintergrasp workshop is contested (#27264)
- [d9eb4c612a](https://github.com/mod-playerbots/azerothcore-wotlk/commit/d9eb4c612a8cd486c3d197ead46d2a94dd0d1d10) — fix(Scripts/ICC): make the Lady Deathwhisper elevator wait 7s at each stop (#27149)
- [5c94774263](https://github.com/mod-playerbots/azerothcore-wotlk/commit/5c94774263fb2aa2b68fb1a9fb3dddde12b46e2f) — fix(Scripts/ICC): restore Rocket Artillery in the Gunship Battle (#27199)
- [27e0962041](https://github.com/mod-playerbots/azerothcore-wotlk/commit/27e0962041c1ca6ffa3abaed776b46ed8b486e76) — fix(Core/Spells): correct SPELLMOD\_COST order of operations (#27388)
- [55794ee4bc](https://github.com/mod-playerbots/azerothcore-wotlk/commit/55794ee4bc56ef26627730f0e751135b84fa462c) — fix(Core/Events): validate holiday data when loading game events (#27652)
- [f9991e7607](https://github.com/mod-playerbots/azerothcore-wotlk/commit/f9991e7607816b4072f500519b66b2e1ca97e3da) — fix(Scripts/DarkmoonFaire): play the faire music (#27653)
- [bea83ff495](https://github.com/mod-playerbots/azerothcore-wotlk/commit/bea83ff495c452c1be1701dc018ffbfb003729ea) — fix(Scripts/ICC): Restore Svalna's spear kill on the Argent captains (#27233)
- [1daf88e20a](https://github.com/mod-playerbots/azerothcore-wotlk/commit/1daf88e20a69c6da5f38a424c5f547a49eb96d67) — fix(Scripts/Quest): start City of Light escort movement (#27245)
- [542ae701e2](https://github.com/mod-playerbots/azerothcore-wotlk/commit/542ae701e2adf4110903c61a600a0189e0149a58) — fix(Scripts/Icecrown): Crusaders' Pinnacle waves emerge from the ground and charge the banner (#27090)
- [7b049e14e8](https://github.com/mod-playerbots/azerothcore-wotlk/commit/7b049e14e87d5da31fccef4343ca4ecd74cf943d) — fix(DB/Creature): let Winterfall Runners attack nearby players (#27658)
- [21d96f137b](https://github.com/mod-playerbots/azerothcore-wotlk/commit/21d96f137bdca921671e122c5fbba6b03715c329) — fix(DB/Events): Implement Argent Tournament construction quest behavior. (#27650)
- [e9517efd29](https://github.com/mod-playerbots/azerothcore-wotlk/commit/e9517efd290dccc41a08dc32bdc5246021cf3e64) — fix(Scripts/Spells): Mage T8 4P bonus never preserving procs (#27637)
- [1cee197ffa](https://github.com/mod-playerbots/azerothcore-wotlk/commit/1cee197ffa3fe7431d4cdffc23a938bba3e809c7) — chore(DB): import pending files
- [44caddaa47](https://github.com/mod-playerbots/azerothcore-wotlk/commit/44caddaa4796b5a9696f5430720aa546c4980e0a) — fix(DB/SAI): Make some aggro talk lines speak from self (#27649)
- [386f3a13fa](https://github.com/mod-playerbots/azerothcore-wotlk/commit/386f3a13fabe80da69dc4fce9f4aac5ec93ad293) — chore(DB): import pending files
- [d56db46693](https://github.com/mod-playerbots/azerothcore-wotlk/commit/d56db466932609a52160683839c0c53803503c4f) — feat(Codestyle/SQL): require both id and guid in spawn UPDATEs (#27657)
- [f68a9759bd](https://github.com/mod-playerbots/azerothcore-wotlk/commit/f68a9759bdd11e773876c6bc12fd0c74866422c2) — fix(Scripts/Feralas): summon the Gordunni trap mound through its own spells (#27632)
- [5b04ee5469](https://github.com/mod-playerbots/azerothcore-wotlk/commit/5b04ee546927ff28bc2f3ee765de623a64eafdc3) — feat(Scripts/ICC): script the Deathbringer Saurfang outro and victory… (#27112)
- [22cb0fb87f](https://github.com/mod-playerbots/azerothcore-wotlk/commit/22cb0fb87f132a19601c5093111363223302eba1) — chore(DB): import pending files
- [4d4ae4f957](https://github.com/mod-playerbots/azerothcore-wotlk/commit/4d4ae4f95753f705c9745cfd9e8e8dd7aba772f9) — fix(Scripts/Ulduar): stop Freya's Ward adds despawning on the spell timer (#27651)
- [a7ba226a68](https://github.com/mod-playerbots/azerothcore-wotlk/commit/a7ba226a68fd07089d5c61d984c3120770ed7b69) — fix(Scripts/BlackrockDepths): script the Black Vault warder event (#26973)
- [34d9caf53a](https://github.com/mod-playerbots/azerothcore-wotlk/commit/34d9caf53a05cc5079a36deb1b4e9bb3cc3d545e) — chore(DB): import pending files
- [f8cc92b5c0](https://github.com/mod-playerbots/azerothcore-wotlk/commit/f8cc92b5c03e897700265aff3a251f097b0f0aff) — fix(DB/Quest): Correct Report to Kadrak gating (#27418)
- [7883cb5d1c](https://github.com/mod-playerbots/azerothcore-wotlk/commit/7883cb5d1c46c5a4148d8fbcdc78bd3ac393eaf1) — chore(DB): import pending files
- [6c25009b20](https://github.com/mod-playerbots/azerothcore-wotlk/commit/6c25009b20747a0cf65e14bacab0c90128502417) — fix(Core/Events): match a holiday to its own event, not its building stage (#27655)
- [2fb90e3b1f](https://github.com/mod-playerbots/azerothcore-wotlk/commit/2fb90e3b1f6d3cea2a552f12a69aa5e24d195061) — fix(CI): run the checks when a draft PR is marked ready for review (#27635)
- [0e6a5d322c](https://github.com/mod-playerbots/azerothcore-wotlk/commit/0e6a5d322ca19137801e704ba32748e71b81305b) — fix(Scripts/Ulduar): do not let Thorim's evade despawn trigger his defeat (#27672)
- [41f475e9da](https://github.com/mod-playerbots/azerothcore-wotlk/commit/41f475e9da33a2eeea243f5641d170cbfa11ab4a) — fix(Scripts/Dalaran): Eject only from the quarters (#27666)
- [0171dde577](https://github.com/mod-playerbots/azerothcore-wotlk/commit/0171dde577842d76e420209a927bfa78c8656a06) — fix(Core/Calendar): count only the holidays actually sent (#27669)
- [82d6aee64c](https://github.com/mod-playerbots/azerothcore-wotlk/commit/82d6aee64c11d9c761412d0b71f9e2f4abb18e21) — fix(DB/Creature): Add missing creatures in Stormwind Harbor and update positions to match sniffed data (#27319)
- [3277c205da](https://github.com/mod-playerbots/azerothcore-wotlk/commit/3277c205da2901e3b16f4d4dfde9d7a5260b44d5) — chore(DB): import pending files
- [b573d7e613](https://github.com/mod-playerbots/azerothcore-wotlk/commit/b573d7e613878a80aa8d01033608e83fe962e60d) — perf(Core): derive creature terrain status lazily; cache GetScriptId() (#27445)
- [86b8504097](https://github.com/mod-playerbots/azerothcore-wotlk/commit/86b8504097b516283997f88154bf436660df33a7) — fix(DB/Creature): show the Dark Iron Tunneler health bars (#27667)
- [cd8610a191](https://github.com/mod-playerbots/azerothcore-wotlk/commit/cd8610a191d66da67cfa40da6b28b70569a9cc88) — chore(DB): import pending files
- [8337a378ac](https://github.com/mod-playerbots/azerothcore-wotlk/commit/8337a378ac325e62a6a91e00c6a5e944205e8536) — fix(Scripts/ICC): spawn Keleseth Shadow Resonance adds away from him (#27661)
- [730868dac8](https://github.com/mod-playerbots/azerothcore-wotlk/commit/730868dac8ac176167eeba01066167bf4e7b220c) — chore(skills/generate-pr-description): add a line separator (#27686)
- [e72b221969](https://github.com/mod-playerbots/azerothcore-wotlk/commit/e72b2219697d098f861e91d6c52064bfe20644c9) — fix(Scripts/Commands): Allow negative action IDs in .npc do (#27691)
- [025ede00fe](https://github.com/mod-playerbots/azerothcore-wotlk/commit/025ede00fe786a34ef8e219ea85fc77697e46f28) — Merge pull request #251 from mod-playerbots/test-staging *(fusion)*
- [58bf0f7173](https://github.com/mod-playerbots/azerothcore-wotlk/commit/58bf0f7173c09cf53c26c0f404ea9e86b9d6b514) — Merge branch 'master' of https://github.com/kadeshar/azerothcore-wotlk into 20260918-ac-sync *(fusion)*
- [3f3e90f94c](https://github.com/mod-playerbots/azerothcore-wotlk/commit/3f3e90f94c660cbc531dba28b715eb48049d7a6d) — Merge pull request #252 from kadeshar:20260918-ac-sync *(fusion)*
- [f15ad94949](https://github.com/mod-playerbots/azerothcore-wotlk/commit/f15ad94949e3267f6fdf01b77b93f6189afc4944) — Strip Playerbot database core changes
- [3b9011d84b](https://github.com/mod-playerbots/azerothcore-wotlk/commit/3b9011d84b3239431b83b1b72d1b995cc483e537) — Merge pull request #253 from mod-playerbots/core/strip-playerbots-database *(fusion)*
- [a53ddd813e](https://github.com/mod-playerbots/azerothcore-wotlk/commit/a53ddd813ea11c11ca442171df987cb9816c2f5c) — fix(Scripts/Ulduar): stop Freya's Tidal Wave when its cast is kicked (#27636)
- [24adf9bfed](https://github.com/mod-playerbots/azerothcore-wotlk/commit/24adf9bfed23f490f1fe46549e579cf3e4b82f80) — chore(DB): import pending files
- [7ce4e9bf62](https://github.com/mod-playerbots/azerothcore-wotlk/commit/7ce4e9bf62bfb603740fb4a797facbe36bfb1d2e) — Change Position declaration to struct in SpellDefines
- [7eeef5223b](https://github.com/mod-playerbots/azerothcore-wotlk/commit/7eeef5223bc3553470655e55d877853835c43dc2) — test(e2e/Ulduar): drop the flaky Brightleaf sun beam despawn test (#27703)
- [da89adec12](https://github.com/mod-playerbots/azerothcore-wotlk/commit/da89adec127e918f7572ee7bd820679b27710c9e) — fix(Scripts/AQ40): Prevent Twin Emperors & C'Thun engagement without required bosses (#13395) (#27675)
- [3df225f8cb](https://github.com/mod-playerbots/azerothcore-wotlk/commit/3df225f8cb890379816794c6699cdc891d738e79) — fix(Scripts/Ulduar): spawn Immortal Guardians at full Empowered size (#27692)
- [cb6660f695](https://github.com/mod-playerbots/azerothcore-wotlk/commit/cb6660f69520dcd53132d529cff29f494e9f5e10) — fix(Core/SmartAI): Avoid duplicate scripts for creatures with no GUID AI (#27685)
- [7838cd733b](https://github.com/mod-playerbots/azerothcore-wotlk/commit/7838cd733b2fd6a2c09cb9decaaae76eb13f8c12) — fix(Scripts/ICC): stop marking the Blood Prince Council as failed on spawn (#27665)
- [1fefa78338](https://github.com/mod-playerbots/azerothcore-wotlk/commit/1fefa78338dbb8677ed4af3a41bef518a97afae0) — fix(Scripts/Ulduar): Corrupted Wisdom for JoTW paladins (#27712)
- [6e899588f6](https://github.com/mod-playerbots/azerothcore-wotlk/commit/6e899588f6902dd15062f5a22d1e42e385939e5d) — fix(Scripts/Karazhan): Set Terestian Illhoof to \_JustEngagedWith() upon Pull (#27642)
- [e622cb5157](https://github.com/mod-playerbots/azerothcore-wotlk/commit/e622cb515772932325c0eca54c71ca0f8408dab7) — fix(DB/Ulduar): correct Freya's Gift emblem counts (#27718)
- [70865b66c7](https://github.com/mod-playerbots/azerothcore-wotlk/commit/70865b66c758679a898bececbd8e08c4100b9f2b) — chore(DB): import pending files
- [fd43d2b5ad](https://github.com/mod-playerbots/azerothcore-wotlk/commit/fd43d2b5adcd358a2493ab5c3d6e6b0f90edaba8) — fix(Core/Scripting): ask OnPlayerCanLearnSpell before a trainer casts an entry (#27707)
- [1936f6cd81](https://github.com/mod-playerbots/azerothcore-wotlk/commit/1936f6cd81d56f27bcd4b230fde40abfe9ba0584) — fix(Core/Unit): prevent dangling AbstractFollower pointer crash in ~Unit() (#27726)
- [3adbe20d0b](https://github.com/mod-playerbots/azerothcore-wotlk/commit/3adbe20d0be267963042e064d4229b12f7498aff) — Merge branch 'azerothcore:master' into test-staging *(fusion)*
- [7f12e89ee5](https://github.com/mod-playerbots/azerothcore-wotlk/commit/7f12e89ee5f467a50e62eba1d525eac7dc953d03) — Merge pull request #254 from mod-playerbots/test-staging *(fusion)*

## playerbots — 26 commits

Base : `b6696bdbd3740e575598d167d69f39f68cc0b907` ; tête : `7bae1b5c58c76a0aa20381155edc08096d1485b2`.
[Comparaison complète](https://github.com/mod-playerbots/mod-playerbots/compare/b6696bdbd3740e575598d167d69f39f68cc0b907...7bae1b5c58c76a0aa20381155edc08096d1485b2).

- [a22a11c2b0](https://github.com/mod-playerbots/mod-playerbots/commit/a22a11c2b08e27f3156c4cb8e51cafd1aa03434c) — fix(chat): guard null PlayerbotAI in HandleCommand (#2713)
- [7e9de98232](https://github.com/mod-playerbots/mod-playerbots/commit/7e9de98232cf16bba33f75c348d8ae86bfa9b245) — Fix a crash from a whispered item command, and a wrapping loop counter in parseMoney (#2727)
- [a224093b6b](https://github.com/mod-playerbots/mod-playerbots/commit/a224093b6b638ee4083d19f51cf9df7b6cae41e2) — Rewrite Vigilance action and trigger to target dps and change inheritance (#2771)
- [c336f9b872](https://github.com/mod-playerbots/mod-playerbots/commit/c336f9b872af8bf00603c3326a3515704fe246dc) — fix(rpg): let a wandering bot pick up the quest it is standing next to (#2696)
- [2c1a704005](https://github.com/mod-playerbots/mod-playerbots/commit/2c1a7040056bc6e1684f51cf7ce24a7c63b5eef5) — Align Rogue strategy names with specs (#2766)
- [3a8a6e4afc](https://github.com/mod-playerbots/mod-playerbots/commit/3a8a6e4afc5a554311d2473e383c3974a3d3224c) — Gruul Bug Fixes & Improvements (#2751)
- [619a06fc79](https://github.com/mod-playerbots/mod-playerbots/commit/619a06fc795a7220fd5c27d5a5aa4a2fbe383b72) — Rewrite Zul'Aman Strategy (#2762)
- [9a12dc79db](https://github.com/mod-playerbots/mod-playerbots/commit/9a12dc79db155d5c39e4a040eba07f9ccb478dcf) — Merge pull request #2779 from mod-playerbots/test-staging *(fusion)*
- [323aa579fe](https://github.com/mod-playerbots/mod-playerbots/commit/323aa579fe772640a086dfb460b9de0f680bd846) — feat(pmon): dump the performance monitor to JSON alongside the table (#2740)
- [4710eb3fba](https://github.com/mod-playerbots/mod-playerbots/commit/4710eb3fba7fce9e47f7321dc422441417ddad99) — Remove forward declarations that required core changes. (#2767)
- [09a8047554](https://github.com/mod-playerbots/mod-playerbots/commit/09a80475540d931d11bc7568a8322aef5ea338bf) — fix(bots): make random bot account/character deletion work with a remote login database (#2772)
- [c7fc27945c](https://github.com/mod-playerbots/mod-playerbots/commit/c7fc27945c0773528f8563a609a46dd7649561aa) — fix(Bot): re-anchor m\_lastFallZ so LFG can teleport bots into a dungeon (#2754)
- [80a6fbe612](https://github.com/mod-playerbots/mod-playerbots/commit/80a6fbe6125bf58b4fc8c24b3c6070278730b1c3) — AI agents, code review guidelines, and coderabbit. (#2769)
- [67abdbd6c9](https://github.com/mod-playerbots/mod-playerbots/commit/67abdbd6c9c328e6a5d70bbfd7d731ac3f78ea0b) — Rename isLogingout to IsLoggingOut (#2788)
- [463b52708f](https://github.com/mod-playerbots/mod-playerbots/commit/463b52708fa226a7e705bdd8c2eb464f7b95bc53) — Update Magtheridon Strategy (#2791)
- [e7aa1395f1](https://github.com/mod-playerbots/mod-playerbots/commit/e7aa1395f102ab6285e528d5c376759ecb18a8b4) — Add Hellfire Citadel: Hellfire Ramparts dungeon strategy (#2776)
- [d2aba0fe48](https://github.com/mod-playerbots/mod-playerbots/commit/d2aba0fe48b3366a90094f1762c9078b5210eeb7) — Rewrite Hyjal Strategy (#2760)
- [75d735631a](https://github.com/mod-playerbots/mod-playerbots/commit/75d735631a788883d87b9419934dedce2dbf33a6) — Rewrite Tempest Keep: The Eye Strategies (#2755)
- [32a003e119](https://github.com/mod-playerbots/mod-playerbots/commit/32a003e1193966803192d0ac7fb52e9c417dc10a) — Limit gem socketing via maintenance to gems that do not exceed the equipment quality (#2795)
- [3f7b6abd1e](https://github.com/mod-playerbots/mod-playerbots/commit/3f7b6abd1e3a96d01c376efafb3d008aeaafcffa) — fix: stop cyclic craft chains from overflowing the stack (#2777)
- [af078829ef](https://github.com/mod-playerbots/mod-playerbots/commit/af078829ef15ecd64ccf0714214c6e2c9c95b7b0) — Use core modular database functionality (#2793)
- [05473d6253](https://github.com/mod-playerbots/mod-playerbots/commit/05473d6253d82aa96e5d849837575eac9da30977) — Bots score item by stats only and correctly value Feral weapons (#2790)
- [e36f73054f](https://github.com/mod-playerbots/mod-playerbots/commit/e36f73054fedb0127f7d18033474f2723ab4d35a) — fix(pmon): measure every calculated value, not just the target pickers (#2738)
- [e9cc38cc04](https://github.com/mod-playerbots/mod-playerbots/commit/e9cc38cc046184d91aceccc71e4e2760cc18be7d) — Dungeon Clean-ups (#2803)
- [64f14c34f4](https://github.com/mod-playerbots/mod-playerbots/commit/64f14c34f466bd8315dd012ca7be7f4eba309edd) — Macos build fix (#2809)
- [7bae1b5c58](https://github.com/mod-playerbots/mod-playerbots/commit/7bae1b5c58c76a0aa20381155edc08096d1485b2) — Merge pull request #2804 from mod-playerbots/test-staging *(fusion)*

## dungeon-clear — 9 commits

Base : `98929d6d261e615aa410f8e2d08ca83710fd2cad` ; tête : `805b909c7286348e75d0561f8cc259750e6ae62b`.
[Comparaison complète](https://github.com/jrad7/mod-dungeon-clear/compare/98929d6d261e615aa410f8e2d08ca83710fd2cad...805b909c7286348e75d0561f8cc259750e6ae62b).

- [c5f31b3353](https://github.com/jrad7/mod-dungeon-clear/commit/c5f31b3353376e40c094d08fbc8fd8832d2a8425) — feat(toc): implement Trial of the Champion (map 650)
- [af5fe2f3d1](https://github.com/jrad7/mod-dungeon-clear/commit/af5fe2f3d111ced7add215c9696e819aa792ea83) — fix(nav): route straight on maps the core runs without pathfinding
- [29c53fd10f](https://github.com/jrad7/mod-dungeon-clear/commit/29c53fd10f99c1f978fba6cecd326bde8ce170d9) — feat(oculus): implement The Oculus (map 578)
- [fbaaf0d845](https://github.com/jrad7/mod-dungeon-clear/commit/fbaaf0d845ceed5c4ee9d70aac1b9635735ff32a) — fix(test): drop the GetPlayerbotsDBRevision mock the core no longer declares
- [b77502ffc9](https://github.com/jrad7/mod-dungeon-clear/commit/b77502ffc9b009d4272e096aa5a5b3efa775ff8a) — Merge fix/drop-playerbots-db-revision-mock *(fusion)*
- [b5ccac14a8](https://github.com/jrad7/mod-dungeon-clear/commit/b5ccac14a82aa5c06036ca431c2c8a885eedcea9) — fix(data): make BossSpawnIndex and DungeonSpawnGraph init thread-safe
- [3fb3566bec](https://github.com/jrad7/mod-dungeon-clear/commit/3fb3566beccd4f823dd10edf927ef2587e2923f5) — Merge fix/boss-index-thread-safe-init *(fusion)*
- [cd92565384](https://github.com/jrad7/mod-dungeon-clear/commit/cd9256538473dcd2b0f04346e79b1b7b09a09231) — fix(compat): build on the upstream-shaped playerbots core
- [805b909c72](https://github.com/jrad7/mod-dungeon-clear/commit/805b909c7286348e75d0561f8cc259750e6ae62b) — Merge fix/playerbots-core-alignment *(fusion)*

## hermes — 51 commits

Base : `bcacff9505a3da2f12c18a896461f5e901f7d367` ; tête : `5ea8767f0edbd1496511053d7a30136f7963b0a5`.
[Comparaison complète](https://github.com/Xian55/HermesProxy/compare/bcacff9505a3da2f12c18a896461f5e901f7d367...5ea8767f0edbd1496511053d7a30136f7963b0a5).

- [7a995c8f80](https://github.com/Xian55/HermesProxy/commit/7a995c8f806a0ea382a775551e10a76e7dc9f9f5) — Source-generated packet dispatch: 827 opcodes off runtime reflection (#292)
- [6a1fa484b8](https://github.com/Xian55/HermesProxy/commit/6a1fa484b8afdbc1fd97390954a1b974a257370c) — fix(v343): map 16 opcodes the client sends, and one that was pinned to zero (#293)
- [ed63e30e48](https://github.com/Xian55/HermesProxy/commit/ed63e30e48bf1dfa0c92d378795bcc2e2be56c0c) — fix(v343): answer inspect honour stats with the 3.4.3 layout (#294)
- [c7bb0072c7](https://github.com/Xian55/HermesProxy/commit/c7bb0072c7253f5c1bb2eec5240d485aa5b0cebd) — fix(v343): let raid subgroup drags reach the server (#295)
- [0fda1c195e](https://github.com/Xian55/HermesProxy/commit/0fda1c195e449ffe2b5108e4be4abc46b959fdd6) — docs: CLAUDE.md handbooks for the generated-dispatch folders
- [50c69ee355](https://github.com/Xian55/HermesProxy/commit/50c69ee35509742f907ade20d74f6419e5948513) — docs: retire reflection-era comments in the dispatch code
- [ab90519174](https://github.com/Xian55/HermesProxy/commit/ab90519174f1ad173714e4c89006b0c57b133844) — feat(outbox): one place to send a packet now or hold it until something happens
- [879c05fb0f](https://github.com/Xian55/HermesProxy/commit/879c05fb0f35e6c7badd249102269b89ea1b76cd) — feat(outbox): move socket queues, delayed packets and handler sleeps onto the outbox
- [a55465318d](https://github.com/Xian55/HermesProxy/commit/a55465318d5affcccb862b83e22e6fe7f60a8b40) — feat(outbox): hold data-dependent packets on the outbox instead of session fields
- [6c67b42e59](https://github.com/Xian55/HermesProxy/commit/6c67b42e59bf953ff921576910e1a036dddf1318) — fix(v343): restore the pet frame after a stable swap, a charm or a vehicle
- [6457e0d1bb](https://github.com/Xian55/HermesProxy/commit/6457e0d1bb0e708e939daedb9a269c1c32b70df8) — fix(combat): send the attack stop the client asked for, once the server answers
- [aac7203e96](https://github.com/Xian55/HermesProxy/commit/aac7203e96d7ba1ecfadd17d97510238b9672d1b) — fix(net): keep the legacy link no-delay when the client asks for Nagle
- [9afd886e91](https://github.com/Xian55/HermesProxy/commit/9afd886e91904581fe35c24a7e20b41afe3b3b91) — fix(net): bound the waits that a dead or stalled peer used to hold for ever
- [55d85a3de7](https://github.com/Xian55/HermesProxy/commit/55d85a3de756b238c4f55b31316578890eb038ae) — feat(session): run a session's work on one thread at a time
- [feb64733ce](https://github.com/Xian55/HermesProxy/commit/feb64733ce9070dad7aeeeb540dd31d3926171cf) — fix(group): compare the legacy server's patch, not a build number from another branch
- [276e9c6f86](https://github.com/Xian55/HermesProxy/commit/276e9c6f8636c48565ef7a81a830fa043d7a511d) — refactor(session): run outbox deadlines and socket attach on the session's owner
- [436de55732](https://github.com/Xian55/HermesProxy/commit/436de55732f1ba7ca50b304ead9983e29e0eba6d) — feat(metrics): per-interval max latency and a --metrics-interval flag (#297)
- [9a8a9683ab](https://github.com/Xian55/HermesProxy/commit/9a8a9683ab373ccc4dd5df189413707e88ce51de) — Merge pull request #298 from Xian55/feat/session-outbox-core *(fusion)*
- [e699f88420](https://github.com/Xian55/HermesProxy/commit/e699f8842080fd5336058a7ac52f7f0f7317881c) — fix(characters): preserve declined-name flags from legacy servers (#307)
- [03c7271a85](https://github.com/Xian55/HermesProxy/commit/03c7271a85cb6a7e52e08e1e97101b0861a57043) — feat(characters): handle the ruRU declined-name submission (#308)
- [081d1b9952](https://github.com/Xian55/HermesProxy/commit/081d1b9952889938ff55433096c0a453e4fea07e) — perf(update): build the owner block's arrays on demand (#309)
- [6bb8ffcf07](https://github.com/Xian55/HermesProxy/commit/6bb8ffcf07a9dbbc9df5e2d264b35fe60d9c5d4e) — fix(battlegrounds): fire Eye of the Storm's tower triggers by proximity (#310)
- [c727acd3d6](https://github.com/Xian55/HermesProxy/commit/c727acd3d6becefbc286202059caae84cd9ca4b5) — fix(pets): route pet guids by pet\_number in both directions (#311)
- [0bc8e7d087](https://github.com/Xian55/HermesProxy/commit/0bc8e7d0876429bc8de7d5293fc21218c64e275f) — fix(movement): smooth flying splines and detect taxi start (#301) (#312)
- [c7bfdf40ec](https://github.com/Xian55/HermesProxy/commit/c7bfdf40ec1359b92ee96ce2aeec5dca2fdcf9a8) — fix(mail): let pre-3.3.0 letters be read from the bags (#313)
- [daf7a1bb30](https://github.com/Xian55/HermesProxy/commit/daf7a1bb307aa1980152fb2719f84a3e25b31d91) — fix(v3\_4\_3): hold the player's own updates until the client has the player (#315)
- [9a998cd9cd](https://github.com/Xian55/HermesProxy/commit/9a998cd9cdd38688fa8107d059f45272d643da20) — fix(v3\_4\_3): decide an empty Values delta from the descriptor tree (#316)
- [d0103a2dbd](https://github.com/Xian55/HermesProxy/commit/d0103a2dbdd219a81ecc4f20b9807c64384f2093) — fix(v3\_4\_3): read HasPartyIndex bit in roll and minimap ping (#319)
- [41dd336264](https://github.com/Xian55/HermesProxy/commit/41dd336264ae781d5eaf4f1a0ae644505faeaa1e) — perf(v3\_4\_3): compress large packets to the client and frame sends in place
- [28b7923230](https://github.com/Xian55/HermesProxy/commit/28b79232300487b72e87b5f2505385a75fc7652a) — feat: stop the proxy gracefully on a named event
- [583efc24e8](https://github.com/Xian55/HermesProxy/commit/583efc24e8676b2a32d3dc3a4e758459df49d3c4) — perf: remove per-packet boxing and buffer churn from the update path
- [c745a028b2](https://github.com/Xian55/HermesProxy/commit/c745a028b25fd999ddd9b4d05d04ba9d6424528e) — perf: keep serialized packet bytes pooled until they are on the wire
- [7cd3ebb269](https://github.com/Xian55/HermesProxy/commit/7cd3ebb2697611583c2c4413453caa7c33e65d02) — perf: name opcodes without rebuilding the enum's name table
- [75a0791fe3](https://github.com/Xian55/HermesProxy/commit/75a0791fe334c023026838054909d1a380d762cf) — perf: translate flags without boxing, and drop per-call delegates
- [c5f6568436](https://github.com/Xian55/HermesProxy/commit/c5f6568436c20768d462a28a256655afe890f4c2) — perf: stop building trace strings and boxing indexes on the update path
- [1c3a2ccb2a](https://github.com/Xian55/HermesProxy/commit/1c3a2ccb2a20825e3662f33d53a78e25fc17c21e) — perf: allocate the rarely-sent half of UnitData and PlayerData on demand
- [63dfc16dbb](https://github.com/Xian55/HermesProxy/commit/63dfc16dbbd8743bbd847c2608298c334ab3aa65) — Merge pull request #321 from Xian55/perf/v343-send-compression *(fusion)*
- [fcf88915d7](https://github.com/Xian55/HermesProxy/commit/fcf88915d7782025f02edf45315daa8a5f2ccf22) — fix: count keyring and currency-token slots as inventory
- [eec392e004](https://github.com/Xian55/HermesProxy/commit/eec392e004622d32ba3bb74822cf3604aaf943a9) — docs: record the keyring quest-item fix in the wotlk matrix
- [27a905ac11](https://github.com/Xian55/HermesProxy/commit/27a905ac11d2eabfd8ef62c0dbddf3a725e1aeb0) — Merge pull request #323 from Xian55/fix/v343-keyring-quest-items *(fusion)*
- [96980e4772](https://github.com/Xian55/HermesProxy/commit/96980e47722336e5d6341cd166f3489a95ddec2c) — perf: stop finalizing packets and building per-block ones nothing fills
- [fe70fd8a0b](https://github.com/Xian55/HermesProxy/commit/fe70fd8a0bb14fb227450496b4d84f4eb0e2a9da) — Merge pull request #324 from Xian55/perf/receive-packet-alloc *(fusion)*
- [5b0ff589ce](https://github.com/Xian55/HermesProxy/commit/5b0ff589ce1f4ec2de1df24a4d55fe1f5cca2d60) — fix: stop one bad update block from costing the whole packet
- [2232c1a9d2](https://github.com/Xian55/HermesProxy/commit/2232c1a9d2327972480f9863fe10b19e96c2b4a1) — Merge pull request #326 from Xian55/fix/values-mask-bounds *(fusion)*
- [5278ddda09](https://github.com/Xian55/HermesProxy/commit/5278ddda093a0216d8d016024da49a7a52c91ae2) — fix(auth): accept cached web credentials during launcher logon
- [ce8c3fe2f0](https://github.com/Xian55/HermesProxy/commit/ce8c3fe2f04b2d765d593cde7ed5ffbb6e6019ed) — fix(logging): honor disabled packet capture on legacy connections
- [6219fc5b2d](https://github.com/Xian55/HermesProxy/commit/6219fc5b2da612b00a538d9998666aa5735f5495) — fix(auth): forget a login ticket once its session is torn down
- [2bbd23b349](https://github.com/Xian55/HermesProxy/commit/2bbd23b349e20795157297e0b78534845a2ef30d) — test: add fake-launcher script for launcher ticket logins
- [04277727b0](https://github.com/Xian55/HermesProxy/commit/04277727b01ca56b5d8e8df6b9ca9a42d1abce6c) — Merge pull request #328 from astgln/fix/launcher-ticket-login *(fusion)*
- [c1961e7a64](https://github.com/Xian55/HermesProxy/commit/c1961e7a649b0156c347f3d76e08b6e1a82b983f) — ci: release merged fork PRs with the repo token
- [5ea8767f0e](https://github.com/Xian55/HermesProxy/commit/5ea8767f0edbd1496511053d7a30136f7963b0a5) — Merge pull request #329 from Xian55/fix/release-fork-prs *(fusion)*

## ah-bot — 1 commits

Base : `a680cc1c98290713e9b3d3289544af78e5186dc1` ; tête : `c11d8318cbd8714a9980f9464f78e07d3d48a70a`.
[Comparaison complète](https://github.com/azerothcore/mod-ah-bot/compare/a680cc1c98290713e9b3d3289544af78e5186dc1...c11d8318cbd8714a9980f9464f78e07d3d48a70a).

- [c11d8318cb](https://github.com/azerothcore/mod-ah-bot/commit/c11d8318cbd8714a9980f9464f78e07d3d48a70a) — fix: allow items matching any enabled acquisition source (#166)

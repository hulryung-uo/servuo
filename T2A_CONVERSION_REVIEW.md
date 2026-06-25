# ServUO → T2A (The Second Age) 변환 검토 보고서

> 대상: `/Users/dkkang/dev/uo/servuo` (현재 expansion = **EJ / Endless Journey**)
> 목표: 정확한 **T2A (1998~1999, pre-Renaissance, Felucca-only, pre-AOS)** 셰드
> 검토 방식: 6개 영역 서브에이전트 병렬 전수 검토 (read-only, 코드 수정 없음)

---

## 0. 핵심 요약 (TL;DR)

ServUO는 **이중 경로(dual-path) 설계**라서, 거의 모든 시대별 동작이 `Core.AOS`/`Core.ML`/`Core.SA` 등의 조건문으로 갈라져 있다.
`Expansion` enum이 순서값(T2A=1 < AOS=5)이고 `Core.AOS => Expansion >= AOS`이므로,

> **`Config/Expansion.cfg`의 `CurrentExpansion=EJ` → `T2A` 한 줄 변경**이 수백 개의 조건문을 한 번에 클래식 경로로 전환한다.

이 한 줄이 자동으로 처리하는 것: OPL(툴팁) off, 보험 off, account gold off, AOS 데미지/스탯 공식 off, 특수기술 off, 마법학교(네크로/기사도 등) 미등록, 5캐릭터 슬롯, 휴먼 전용, Felucca 시작도시 테이블 등.

**그러나 expansion 플래그만으로 부족하다.** 다음은 expansion에 의해 게이트되지 **않는** 영역이라 별도 수정이 필요하다:
- 맵 등록 (6개 facet 전부 등록됨)
- 문게이트 생성, 디코레이션, 챔피언 스폰 enable
- 펫 본딩, Factions 자동 활성화 함정, 일부 cfg 토글
- 스폰 데이터(felucca.xml)의 후기 시대 크리처 혼입

작업 비중: 대략 **설정 70% + 코드/데이터 수정 30%**.

---

## 1. 단일 마스터 스위치

| 파일 | 변경 |
|---|---|
| `Config/Expansion.cfg:15` | `CurrentExpansion=EJ` → **`CurrentExpansion=T2A`** |

읽는 곳: `Scripts/Misc/CurrentExpansion.cs:13` → `Core.Expansion` 설정 → `Server/Main.cs:143-153`의 `Core.AOS/UOR/...` 임계값 결정.

---

## 2. CORE / CONFIG / 로그인 / 캐릭터 생성

### 이미 올바르게 동작 (T2A 설정 시 자동)
- `Server/ExpansionInfo.cs:168-174` — T2A 행: Felucca only, AOS 플래그 없음, 하우징 None.
- `CurrentExpansion.cs:27-46` — OPL/보험/account gold/town cryer/AOS 데미지 전부 off.
- `Account.cs:586` — T2A → **5 캐릭터 슬롯**.
- `AccountHandler.cs:33-44` — `StartingCitiesT2A` (전부 Felucca) 테이블 자동 선택.
- `RaceDefinitions.cs` + `CharacterCreation.cs:194` — 휴먼만 선택 가능 (엘프=ML, 가고일=SA).
- `PluginCaps`: `TotalSkillCap=7000(700.0)`, `TotalStatCap=225` — T2A 일치.

### 수정 필요
| 위치 | 문제 | 조치 |
|---|---|---|
| `Scripts/Misc/MapDefinitions.cs:25-33` | **6개 facet 전부 무조건 등록** (Tram/Ilsh/Malas/Tokuno/TerMur 포함). expansion 게이트 없음 | Felucca + Internal만 남기고 나머지 `RegisterMap` 제거/게이트 |
| `Config/PlayerCaps.cfg` | `StrCap/DexCap/IntCap=125`는 AOS 값. T2A 개별 스탯캡은 **100** | 100으로 변경 (기존 캐릭은 마이그레이션 필요) |
| `AccountHandler.cs:35` | `StartingCitiesT2A` 첫 도시가 **"New Haven"** (2008 ML 지역, 시대착오) | 클래식 도시(Britain 등)로 교체 |
| `Config/Client.cfg:5` | `AllowEC=true`, 클라 버전 하한 없음 | 클래식 2D 전용 원하면 `AllowEC=false` + `ClientVerification.Required` 설정 |
| `CharacterCreation.cs:344,386` | 90포인트/120스킬 "고급 캐생"은 post-T2A. expansion 미게이트(클라 패킷 플래그 기반) | `!Core.UOR`일 때 80/100 강제 |
| `CharacterCreation.cs:65` | 시작 골드 1000 하드코딩 | (선택) T2A 스타터로 조정 |

---

## 3. SKILLS & STATS

### 권위 있는 스킬 목록
- `Server/Skills.cs` — `SkillName` enum (28-88), `SkillInfo.m_Table` (594-654), 길이 **58 하드코딩**.
- 클라가 cliloc로 스킬명을 그리므로 **서버는 expansion별 스킬 필터링을 하지 않는다.** 58개 전부 스킬창에 표시됨.
- ⚠️ **enum 번호 변경/축소 금지** — 세이브와 클라 ID에 고정됨.

### post-T2A 스킬 (ID ≥ 49, 비활성화 대상)
Necromancy(49), Focus(50), Chivalry(51), Bushido(52), Ninjitsu(53), Spellweaving(54), Mysticism(55), Imbuing(56), Throwing(57) — `Server/Skills.cs:79-87`, 테이블 645-653.
- 추가 검토: **Remove Trap(48)** — 진짜 T2A에선 독립 스킬 아님(Tinker/Lockpick 기능).
- 권장 비활성 방법(enum 불변): `CharacterCreation.cs:213-217` 루프에서 해당 스킬 `Cap=0` 강제 + 로그인 정규화.

### 캡/스탯 (`Config/PlayerCaps.cfg`)
| 항목 | 현재 | T2A |
|---|---|---|
| SkillCap (개별) | 1000 | 1000 ✅ |
| TotalSkillCap | 7000 | 7000 ✅ |
| TotalStatCap | 225 | 225 ✅ |
| **StrCap/DexCap/IntCap** | **125** | **100** ⚠️ |
| EnableAntiMacro | False | True (검토) |
| EnablePlayerStatTimeDelay | false | true (검토) |

### 시대 미게이트(수정 필요) 스킬 메커닉
- **GGS (Gain-Gain-System, AOS식 따라잡기 보정)** — `SkillCheck.cs:774-806`, `GGSActive = !Siege.SiegeShard`. **expansion 미게이트 → T2A에서도 켜짐.** `!Core.AOS`로 게이트 권장.
- 나머지(스킬 게인 실패 시 획득, 구 스탯게인, 명상/은신/조련/스틸링 등)는 이미 `Core.*` 게이트로 T2A 분기 정상.

---

## 4. SPELLS & COMBAT

> 결론: 거의 전부 `Core.AOS` 이중 경로. `CurrentExpansion=T2A`로 대부분 자동 클래식化. 공식 재작성 불필요.

### 마법 학교 (`Scripts/Spells/Initializer.cs`)
- **Magery 8서클**: 무조건 등록(10-87) — 유지.
- **전부 이미 게이트되어 T2A에서 미등록**: Necromancy(89-110, `Core.AOS`), Chivalry, Bushido/Ninjitsu(`Core.SE`), Spellweaving(`Core.ML`), Mysticism/Imbuing(`Core.SA`), Masteries(`Core.SA/TOL`). 코드 수정 불필요.
- 잔여 정리(선택): 스킬창에 학교 스킬 잔존 → 숨김 + 벤더/룻에서 해당 스펠북·스크롤·리에이전트 제거.

### Magery 메커닉 (이미 클래식)
- 고정 주사위 데미지(예: Fireball `Random(10,7)`, EBolt `Random(24,18)`, FlameStrike `Random(27,22)`).
- `GetDamageScalar`(`Spell.cs:444-479`, `!Core.AOS`): EvalInt vs MagicResist, Magery 보너스, **NPC 대상 ×2**.
- 리에이전트 고정 소비, 서클별 마나 테이블 `{4,6,9,11,14,20,40,50}`, 캐스트딜레이 `0.5+0.25*circle`, 피격 시 시전 방해(클래식), 데미지 딜레이 0.5s.
- `AOS.Damage`: `!Core.AOS`에서 단일 데미지·속성 분할 없음·저항 없음.

### 전투 (`BaseWeapon.cs`, 전부 `Core.AOS` 분기)
- 스윙속도: 클래식 `15000/(Stam+100)*Speed`.
- 적중률: 클래식 `(atk+50)/((def+50)*2)`.
- 데미지: `ScaleDamageOld` (전술/해부/힘/벌목/우수품/내구), **PvP 데미지 절반** 규칙.
- **특수기(WeaponAbility)**: `!Core.AOS`에서 전부 null → 완전 비활성. ✅
- AOS 무기 속성(Hit Fireball/Lightning/Leech/Area): `Core.AOS`에서만 → off.
- **클래식 독무기(충전식)**: `BaseSword/Knife/Spear`에서 `!Core.AOS` 게이트 → T2A에서 정상 작동.
- 붕대 힐 타이밍/사거리: 클래식 분기 정상.

### 리스크
- 무기 서브클래스가 `OldMinDamage/OldMaxDamage/OldSpeed` 등을 정의해야 T2A 수치 정확 → **무기 클래스 Old* 값 감사 필요.**
- `OnHit`의 일부 미게이트 훅(Slayer, Enemy of One 등) — T2A에선 사실상 inert지만 엄밀히는 게이트 권장.

---

## 5. ITEMS / CRAFTING / LOOT / VENDORS

### AOS 아이템 속성
- 표시: `ObjectPropertyList.Enabled=Core.AOS` → off, 구 단일클릭 정보로 폴백.
- **방어구 구 AR**: `BaseArmor.ArmorRating`(418-467) 항상 동작 ✅. 5속성 저항은 미표시.
- ⚠️ **GAP**: `BaseArmor.DistributeExceptionalBonuses`(3165-3217)가 **무조건** 호출(3137-3140) → 우수품 제작 시 AOS 속성저항 14~15점 부여. **`Core.AOS` 게이트 추가 필요.**

### Crafting (post-T2A 기능)
| 기능 | 시대 | 게이트 상태 |
|---|---|---|
| BOD (대량주문서) | UOR | ⚠️ 벤더 지급(`BaseVendor.cs:2230-2238`)이 **시대 미게이트** → 억제 필요 |
| Runic 툴 | AOS | 적용은 `Core.AOS` 게이트, 아이템 자체는 잔존 → BOD 보상서 제거 |
| Imbuing | SA | ⚠️ 코어에 `Core.SA` 가드 미확인 → 스킬 접근 차단 검증 |
| 우수품 속성저항 | AOS | ⚠️ 미게이트(위 참조) |
| Repair/Enhance | AOS/ML | 이미 게이트 ✅ |

### Loot (희소식: 구 T2A 매직아이템 시스템 완비, `Core.AOS=false`면 자동)
- `LootPack.cs:657-794` `Mutate`: `Core.AOS` 분기. **비-AOS(714-759)** = T2A 시스템(Accuracy/Damage/Durability Level, Silver/그룹 슬레이어).
- 구 enum(`WeaponEnums.cs`, `ArmorEnums.cs`)·`Old*` 룻팩 완비 → 자동 선택.
- 정리: 슬레이어 명칭 풀 중 후기 시대(Fey/Eodon 등) 제한, 드랍률 T2A 튜닝(선택).

### Item ID / 미감정 아이템
- `ItemIdentification.cs` + `BaseWeapon/Armor.Identified` 플래그 완비 ✅. 미감정 시 매직레벨 숨김.
- ⚠️ 룻 생성 아이템이 `Identified=false`로 스폰되는지 테스트 필요.

### Vendors
- 대부분 `Core.AOS/SE` 게이트(네크로 리에이전트 등).
- ⚠️ **GAP**: `SBMystic.cs` — 미스티시즘 책/스크롤 **시대 미게이트** → T2A에 노출. 가드 추가. 전체 SB 파일 감사 권장.
- `UseVendorEconomy=Core.AOS` → T2A 자동 off(클래식 재입고).

---

## 6. WORLD / MAPS / REGIONS / SPAWNS / MONSTERS / MOONGATES

> ⚠️ **가장 큰 함정: 이 영역은 expansion 플래그로 게이트되지 않는다.** 맵 등록/문게이트/챔프/디코레이트 전부 수동 수정.

### 맵 (Felucca + Lost Lands로 제한)
- `MapDefinitions.cs:7-54` — 6개 facet 무조건 등록, **게이트 없음**. Tram/Ilsh/Malas/Tokuno/TerMur `RegisterMap` 제거.
- **Lost Lands는 Felucca 맵 파일(map0) 안에 포함** → 별도 facet 불필요. (클라 맵파일에 Lost Lands 지형 있어야 함.)

### 문게이트
- `PublicMoongate.cs` `[MoonGen]`(34-52) — 6개 맵 게이트 생성, Siege일 때만 Tram 스킵. **Felucca만 생성하도록 수정.**
- `MoongateGump`의 `UORLists`(383,568)에 **Trammel 잔존** → Felucca 전용 목적지 목록 필요.
- "(New) Magincia" 목적지(329)는 post-T2A.

### 스폰
- 활성: **XmlSpawner2**. 데이터: `Spawns/*.xml`.
- `felucca.xml`(~2256) 유지 / `trammel.xml`·`ilshenar/tokuno/malas/termur/Eodon/...xml` **drop**.
- `RevampedSpawns/`(Despise/Shame/Wrong/Blackthorn) = SA 리뉴얼 **drop**.
- ⚠️ **`felucca.xml` 자체가 혼합 시대**: juka(ML)·savage(AOS)·solen(ML) 등 후기 크리처 포함 → **필터링 패스 필요** (ophidian/terathan/titan/cyclops/ettin은 정당한 T2A Lost Lands).

### 챔피언 스폰 / 파워스크롤 (Renaissance, post-T2A)
- `ChampionSystem.cs:74` — 런타임은 `Champions.cfg`의 `Enabled`만 본다. **expansion 미게이트.** → `Champions.cfg` `Enabled=false`.
- ⚠️ `BaseChampion.GivePowerScrolls()`(148-230)는 `Map==Felucca`만 체크, 시대 무관 → 챔프 유지 시 스크롤 제거하려면 코드 수정.

### post-T2A 크리처 (스폰 참조 제거로 차단)
SE/Tokuno(Hiryu/Oni/Yamandon...), SA(가고일 전투변종/StygianDrake/보스), AOS(Exodus, 광석 엘리멘탈, ChaosDragoon), ML(Juka/Meer/Solen/peerless 보스), HS(Savage), Void Creatures 전체, Phoenix/EtherealWarrior/Paragon 등.
- 크리처 클래스는 컴파일되므로 **스폰 참조를 지워야 실제로 안 나온다** (expansion 설정만으론 부족).

### 지역/디코레이션
- `Region.cs:1224` — region별 `expansion` 속성은 게이트됨 ✅. 단 Facet 블록 자체는 미게이트 → 비-Felucca facet 제거 권장 (`Data/Regions.xml`).
- `Decorate.cs:35-40` — 6개 맵 무조건 디코레이트, **미게이트.** Felucca만 생성하도록 수정. `DecorateMag/DecorateSA` 실행 금지.

---

## 7. GAMEPLAY 시스템 & 기능

### T2A에 없던 시스템 (비활성화)

**A. expansion 설정만으로 자동 off**: 버프바(`Core.ML`), Pet Training(`Core.TOL`), 추상화 길드시스템(`Core.SE`→길드스톤+Order/Chaos 폴백), Town Cryer 이벤트(`Core.TOL`), Huntmaster(`Core.SA`), ML 호위 NPC, Store(런타임 `<TOL`).

**B. cfg 토글 필요 (expansion 미게이트)**:
| 시스템 | 토글 |
|---|---|
| VvV | `VvV.cfg` `Enabled=False` |
| Veteran Rewards | `VetRewards.cfg` `Enabled=False` |
| Daily Rares | `DailyRares.cfg` `Enabled=False` |
| Honesty | `Honesty.cfg` `Enabled=False` (켜지면 세이브마다 1000개 아이템 재생성) |
| City Loyalty | `CityLoyalty.cfg` `Enabled=False` |
| Champion Spawns | `Champions.cfg` `Enabled=false` |
| Store | `Store.cfg` `Enabled=False` |

**C. 코드 수정 필요 (게이트도 cfg도 없음)**:
- ⚠️ **Factions 자동 활성화 함정**: `Faction.cs:88` `Enabled = !ViceVsVirtueSystem.Enabled`. **VvV를 끄면 Factions가 켜진다.** 둘 다 없는 T2A 상태로 만들려면 코드에서 `Settings.Enabled=false` 강제.
- ⚠️ **펫 본딩**: `BaseCreature.cs:434` `const bool BondingEnabled = true` 하드코딩, 시대 체크 없음.
- 챔프 파워스크롤(위), 구 퀘스트 엔진 레지스트리, ML 퀘스트 훅, Seasonal Events 상태 목록, `RestrictRedsToFel`(General.cfg:20).

**이미 off / 무해**: Ethics(Hero/Evil) 하드 off, Heritage Token(아이템뿐), PVP Arena(월드오브젝트 없음).

### T2A에 있었으나 규칙이 다른 시스템
- **하우징**: `BaseHouse.IsAosRules=>Core.AOS`로 대부분 시대 인지(친구/밴 50, 저장 보너스 없음, 클래식 게임프/데드). ⚠️ 단 `HouseFoundation.IsAosRules=>true` 항상 → 커스텀 디자인 자체는 개별 게이트 안 됨. 배치툴이 `[Constructable]`이라 GM/우회 획득 시 커스텀 가능 → 엄밀히 하려면 `BeginCustomize`/배치툴 카테고리/Convert 버튼에 `Core.AOS` 게이트.
- **PvP/노토리에티**: 살인자=5킬(`Mobile.cs:11846`), 단기 8h/장기 40h 감소, 범죄 2분, 살인자 부활 스탯로스 5-15%(`!Core.AOS`), 가드의 레드 공격(`Core.AOS`=false면 즉결) — **전부 T2A 정확**. ⚠️ 단 Young 보호·`RestrictRedsToFel=True`는 post-T2A → 비활성/False.
- **길드**: T2A에서 길드스톤+Order/Chaos로 정상 폴백. 수정 불필요.
- **호위 퀘스트**: 클래식 `BaseEscortable.cs` = 정통 T2A. 유지.

### 리스크
1. **Factions 자동 활성화 함정** — 최대 주의점.
2. **자동 생성 오브젝트는 토글로 안 지워짐** — EJ로 한 번이라도 부팅했다면 Honesty/Daily Rares/City Loyalty 잔존. **클린 월드에서 시작** 권장.
3. Young 시스템은 PlayerMobile/Notoriety에 얽혀 있고 미게이트 → 의도적 비활성 필요.

---

## 8. 권장 작업 순서

1. **`Config/Expansion.cfg` → `T2A`** (마스터 스위치) — 클린 월드(Saves 초기화)에서 시작.
2. **설정 정리**: `PlayerCaps.cfg`(스탯캡 100), VvV/VetRewards/DailyRares/Honesty/CityLoyalty/Champions/Store cfg off, `General.cfg`(RestrictRedsToFel=False), `Client.cfg`.
3. **맵 제한**: `MapDefinitions.cs` Felucca만, `Data/Regions.xml` 비-Fel facet 제거.
4. **월드 생성 수정**: `PublicMoongate.cs`(Fel only), `Decorate.cs`(Fel only).
5. **코드 게이트 추가**: Factions 강제 off, 펫 본딩 off, `BaseArmor.DistributeExceptionalBonuses` `Core.AOS` 가드, GGS `!Core.AOS`, BOD 벤더 지급 억제, `SBMystic` 가드.
6. **스폰 데이터**: `trammel.xml` 등 제외 + `felucca.xml`의 후기 크리처 필터링(또는 정통 T2A 스폰셋 확보).
7. **스킬 정리**: post-T2A 스킬 `Cap=0` (캐릭 생성/로그인), 학교 스펠북·스크롤·리에이전트 벤더/룻 제거.
8. **데이터 파일 확인**: DataPath의 클라 mul이 T2A 지형(Lost Lands 포함, New Haven/New Magincia 없음)인지.
9. **검증**: Old* 무기 수치 감사, 룻 미감정 스폰 테스트, OPL off 단일클릭 표시 회귀 테스트.

---

## 9. 우선순위별 수정 항목 (코드/데이터)

| 우선 | 항목 | 위치 |
|---|---|---|
| 🔴 필수 | expansion=T2A | `Config/Expansion.cfg` |
| 🔴 필수 | 맵 Felucca only | `MapDefinitions.cs:25-33` |
| 🔴 필수 | Factions 강제 off (VvV off의 역함정) | `Faction.cs:88` |
| 🔴 필수 | 챔프/문게이트/디코 Felucca only | `Champions.cfg`, `PublicMoongate.cs`, `Decorate.cs` |
| 🔴 필수 | felucca.xml 후기 크리처 필터 | `Spawns/felucca.xml` |
| 🟠 중요 | 스탯캡 100, GGS off, 본딩 off | `PlayerCaps.cfg`, `SkillCheck.cs:774`, `BaseCreature.cs:434` |
| 🟠 중요 | 우수품 속성저항 게이트, BOD 억제 | `BaseArmor.cs:3137`, `BaseVendor.cs:2230` |
| 🟠 중요 | post-T2A 스킬 Cap=0 | `CharacterCreation.cs:213` |
| 🟡 정확도 | New Haven 시작도시, SBMystic, Young 시스템, 클라 버전 | 각 파일 |
| 🟢 선택 | 슬레이어 풀, 룻 드랍률, anti-macro, 90/120 캐생 | 튜닝 |

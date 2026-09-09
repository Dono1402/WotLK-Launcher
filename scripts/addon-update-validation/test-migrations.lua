-- Synthetic fixtures only: no account, realm, character or user aura data.
local checks = 0
local function check(ok, message)
  checks = checks + 1
  assert(ok, message)
end
local function resetAuctionator()
  Auctionator = {
    Variables = {}, State = {}, Debug = {Message = function() end},
    DatabaseMixin = {}, Groups = {Constants = {Events = {}}},
    Config = {Options = {SELLING_FAVOURITE_KEYS = 'favourites'}, Get = function() return {} end},
  }
  function GetRealmName() return 'SyntheticRealm' end
  function GetAutoCompleteRealms() return {} end
  function CreateAndInitFromMixin(_, data)
    return {data = data, Prune = function() end}
  end
  function LibStub(name)
    assert(name == 'LibSerialize')
    return {Deserialize = function(_, text)
      if text == 'synthetic-valid' then return true, {itemA = 44} end
      return false, nil
    end}
  end
  function CreateFromMixins()
    return {OnLoad = function() end, GenerateCallbackEvents = function() end}
  end
  assert(load(NEW_VARIABLES, '@candidate/Source/Variables/Main.lua'))()
  Auctionator.Variables.GetConnectedRealmRoot = function() return 'SyntheticRealm' end
end

resetAuctionator()
AUCTIONATOR_PRICE_DATABASE = {__dbversion = 6, SyntheticRealm = {itemA = 123}, OtherSyntheticRealm = {itemB = 456}}
local sameRealmData = AUCTIONATOR_PRICE_DATABASE.SyntheticRealm
Auctionator.Variables.InitializeDatabase()
check(AUCTIONATOR_PRICE_DATABASE.__dbversion == 7, 'Migration increments database schema 6 -> 7')
check(AUCTIONATOR_PRICE_DATABASE.SyntheticRealm == sameRealmData, 'Current realm data retained by reference')
check(AUCTIONATOR_PRICE_DATABASE.SyntheticRealm.itemA == 123, 'Current realm price retained')
check(AUCTIONATOR_PRICE_DATABASE.OtherSyntheticRealm.itemB == 456, 'Other realm data retained')
check(Auctionator.Database.data == sameRealmData, 'Database initialized with retained current data')

Auctionator.Variables.InitializeDatabase()
check(AUCTIONATOR_PRICE_DATABASE.SyntheticRealm.itemA == 123, 'Migration idempotent on schema 7')

-- Reproduce the downgrade hazard with the actual installed old source.
assert(load(OLD_VARIABLES, '@installed-10.2.0/Source/Variables/Main.lua'))()
Auctionator.Variables.GetConnectedRealmRoot = function() return 'SyntheticRealm' end
Auctionator.Variables.InitializeDatabase()
check(AUCTIONATOR_PRICE_DATABASE.__dbversion == 6, 'Old addon rejects new schema and resets it')
check(AUCTIONATOR_PRICE_DATABASE.SyntheticRealm.itemA == nil, 'Old addon loses migrated prices without SavedVariables restore')
check(AUCTIONATOR_PRICE_DATABASE.OtherSyntheticRealm == nil, 'Other realm prices also lost on uncoordinated downgrade')

resetAuctionator()
AUCTIONATOR_PRICE_DATABASE = nil
Auctionator.Variables.InitializeDatabase()
check(AUCTIONATOR_PRICE_DATABASE.__dbversion == 7, 'First-time database created with schema 7')
check(type(AUCTIONATOR_PRICE_DATABASE.SyntheticRealm) == 'table', 'First-time realm table initialized')

resetAuctionator()
AUCTIONATOR_PRICE_DATABASE = {__dbversion = 7, SyntheticRealm = 'synthetic-valid'}
Auctionator.Variables.InitializeDatabase()
check(AUCTIONATOR_PRICE_DATABASE.SyntheticRealm.itemA == 44, 'Serialized current realm accepted via library contract stub')

resetAuctionator()
assert(load(NEW_GROUPS_MAIN, '@candidate/Source/Groups/Main.lua'))()
AUCTIONATOR_SELLING_GROUPS = {Version = 1, HiddenItems = {}, CustomGroups = {
  {name = 'FAVOURITES_GROUP', list = {'synthetic-item'}},
  {name = 'Custom Crafting', list = {'synthetic-other-item'}, postingSettings = {duration = 12}},
}}
Auctionator.Groups.Initialize()
check(AUCTIONATOR_SELLING_GROUPS.CustomGroups[1].name == 'FAVOURITES', 'Old favourite name migrated')
check(AUCTIONATOR_SELLING_GROUPS.CustomGroups[1].list[1] == 'synthetic-item', 'Favourite data retained')
check(AUCTIONATOR_SELLING_GROUPS.CustomGroups[2].name == 'Custom Crafting', 'Custom group data not erased by initializer')
check(AUCTIONATOR_SELLING_GROUPS.CustomGroups[2].postingSettings.duration == 12, 'Custom posting data remains stored even though editing UI removed')

AUCTIONATOR_SELLING_GROUPS = {Version = 1, HiddenItems = {}, CustomSections = {{name = 'FAVOURITES_GROUP', list = {}}}}
Auctionator.Groups.Initialize()
check(AUCTIONATOR_SELLING_GROUPS.CustomSections == nil, 'Legacy CustomSections key removed during migration')
check(AUCTIONATOR_SELLING_GROUPS.CustomGroups[1].name == 'FAVOURITES', 'Legacy CustomSections migrated and renamed')

AUCTIONATOR_SELLING_GROUPS = {Version = 1, HiddenItems = {}, CustomGroups = {}}
local emptyGroupsAccepted, emptyGroupsError = pcall(Auctionator.Groups.Initialize)
check(not emptyGroupsAccepted, 'Known conditional failure reproduced: empty custom group list is not guarded')
check(tostring(emptyGroupsError):find('nil') ~= nil, 'Conditional failure comes from missing first group')

-- Execute only the extracted event-registration block, not the addon itself.
local function registrationEvents(source)
  local events = {}
  WeakAuras = {IsRetail = function() return false end}
  loadFrame = {RegisterEvent = function(_, name) events[name] = true end}
  assert(load(source, '@WeakAuras/talent-event-registration'))()
  return events
end
local oldEvents = registrationEvents(WA_OLD_REGISTRATION)
local newEvents = registrationEvents(WA_NEW_REGISTRATION)
check(not oldEvents.ACTIVE_TALENT_GROUP_CHANGED, 'Installed 5.12.8 lacks talent group load event')
check(newEvents.ACTIVE_TALENT_GROUP_CHANGED, 'Candidate 5.12.9 registers talent group load event')
check(newEvents.PLAYER_TALENT_UPDATE, 'Candidate registers talent update event for non-Retail')
check(newEvents.CHARACTER_POINTS_CHANGED, 'Candidate retains character points event')
check(not newEvents.TRAIT_CONFIG_UPDATED, 'Retail-only event not registered in synthetic Wrath branch')

SYNTHETIC_CHECK_COUNT = checks

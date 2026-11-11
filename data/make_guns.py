r"""Writes guns.json (the zombies weapon table) from the rules agreed with the user. The attachment builds stay in
gun_library_raw.jsonl (the F12 gun logger; newest line per gun wins). Usage: python -I make_guns.py"""
import json
import math
import os

HERE = os.path.dirname(os.path.abspath(__file__))

# Mag size by class (user, 2026-11-01): ARs and SMGs 30 whatever mag is fitted, snipers 10, LMGs 75. Pistols and
# shotguns keep their own (not ruled yet). G36C burst only (PaP'd too). MDR 30 (PaP 45, the user 2026-11-10); VSK-94 keeps its real 20 (PaP 30): balanced by damage per bullet, not mag size. Wall prices come with the points system. SA58 mag 20 (PaP 30); MP5 always full auto. PaP mags: ARs 45, SMGs 50, LMGs 100, snipers 15, pistols double, shotguns +50% (own overrides: M1911 16, Glock 33, MP-43 2, Saiga 10, War Machine 6). Reserve 4x mag, PaP'd 5x.
CLASS_MAG = {"ar": 30, "smg": 30, "sniper": 10, "lmg": 75}

# PaP'd mag by class (Claude's recommendation, 2026-11-01): ARs 45, SMGs 50, LMGs 100, snipers 15, pistols double,
# shotguns +50% (rounded); a gun's own "papMag" overrides. Reserve: 4x mag base, 5x the PaP'd mag ("worth it").
CLASS_PAP_MAG = {"ar": 45, "smg": 50, "lmg": 100, "sniper": 15}
RESERVE, PAP_RESERVE = 4, 5
# Reserve by class (user, 2026-11-08: "ammo reserves need to be larger"): the original-deep stocks, mag x this (ARs 30 + 270)
CLASS_RESERVE = {"ar": 9, "smg": 9, "sniper": 8, "pistol": 8, "shotgun": 12, "lmg": 5, "launcher": 6}
# Shotguns never under this reserve (the user, 2026-11-08: the MP-43's 2 x 8 = 16 was far too few)
SHOTGUN_MIN_RESERVE = 60


def pap_mag(cls, mag):
    if cls in CLASS_PAP_MAG:
        return CLASS_PAP_MAG[cls]
    if cls == "pistol":
        return mag * 2
    if cls == "shotgun":
        return math.ceil(mag * 1.5)
    return mag

# id, name, class, base logged?, pap logged?, PaP adds (short), extra
GUNS = [
    ("m1911", "M1911", "pistol", True, True, "Geneburn grips, trigger-guard mount + laser; fires grenades (Mustang & Sally)",
     {"role": "starter", "mag": 8, "reserve": 80, "papName": "Mustang & Sally", "papMag": 16, "forceMag": True}),
    ("m4a1", "M4A1", "ar", True, True, "DeltaPoint, Hanson 13.7in barrel, KX3, DD gas block, HK charging handle", {}),
    ("m16", "M16", "ar", True, True, "same build + M203 (replaces the Viper lower handguard and BCM foregrip; logged 2026-11-01 01:56)", {"fireModes": ["burst"],
     # Its own ammo pool (1 in the launcher / 8 reserve), HUD counter, splash damage entry and reload; EFT's underbarrel toggle
     "papUbgl": {"name": "M203", "mag": 1, "reserve": 8, "damage": "grenade splash (own table entry)"}}),
    ("aug", "AUG A1", "ar", True, True, "same build, stats only", {}),
    ("ak74n", "AK-74N", "ar", True, True, "CAF foregrip, Klassika rail, SRS-02", {}),
    ("aks74u", "AKS-74U", "ar", True, True, "PBS-4 suppressor, Pilgrim rail cover, Pilad P1x42", {}),
    ("sa58", "SA58 (FAL)", "ar", True, True, "Vltor rail + Fortis Shift grip, Extreme Duty dust cover, UTG reflex",
     {"mag": 20, "papMag": 30, "fireModes": ["single"], "papFireModes": ["single"]}),
    ("scarl", "SCAR-L", "ar", True, True, "ROMEO7, SF3P + Mini Monster suppressor, Hera CQR grip", {}),
    ("mp7", "MP7A1", "smg", True, True, "Walther MRS reflex", {}),
    ("ppsh", "PPSh-41", "smg", True, True, "same build, stats only", {}),
    ("sr2m", "SR-2M Veresk", "smg", True, True, "B-17 mount + Aimpoint H-2", {}),
    ("mp5", "MP5", "smg", True, True, "50-round X-5 drum, Tri-Rail + Magpul AFG, Illusion 9 suppressor, MRS",
     {"fireModes": ["fullauto"]}),
    ("ump45", "UMP 45", "smg", True, True, "XPS3, Omega 45k suppressor, BCM foregrip", {}),
    ("vector", "Vector 9x19", "smg", True, True, "Walther MRS, Osprey 9 suppressor, SI Cobra grip", {}),
    ("sv98", "SV-98", "sniper", True, True, "same build, stats only", {}),
    ("m700", "M700", "sniper", True, True, "KeyMod rail + laser, EXPS3 Tan", {}),
    ("sr25", "SR-25", "sniper", True, True, "ROMEO7, Blackout + SDN-6 suppressor, Lancer handguard + TangoDown grip", {}),
    ("svds", "SVDS", "sniper", True, True, "TAPCO grip, Rotor 43 suppressor, modernization handguard, Harris bipod, Osovets grip, laser", {}),
    ("rpd", "RPD", "lmg", True, True, "same build, stats only", {}),
    ("m60", "M60E6", "lmg", True, True, "Hybrid 46 suppressor, Mod 1 handguard, RK-1 grip, UTG reflex", {"fireModes": ["fullauto"]}),
    ("fiveseven", "Five-seveN MK2", "pistol", True, True, "threaded barrel + SFN-57 suppressor, RMR, laser", {"mag": 20}),
    ("glock18c", "Glock 18C", "pistol", True, True, "Pachmayr grip glove, laser, Tiger Shark mount + DeltaPoint, GLR-17 stock",
     {"mag": 17, "papMag": 33, "fireModes": ["single"], "papFireModes": ["fullauto"]}),
    ("deagle", "Desert Eagle L6", "pistol", True, True, "laser", {"mag": 7}),
    ("rsh12", "RSh-12", "pistol", True, True, "laser, Lobaev mount + Razor HD 1-6x", {"mag": 5}),
    ("mp43", "MP-43-1C", "shotgun", True, True, "same build, stats only", {"mag": 2, "papMag": 2}),
    ("m870", "M870", "shotgun", True, True, "Monster Claw brake, PR-870 forestock + KAC grip, SGA stock, MRS", {"mag": 7, "papMag": 10}),
    # Saiga: its 5-round mag emptied in under a second on full auto; a forced 16 (PaP 24), 164 reserve (the user, 2026-11-08)
    ("saiga12", "Saiga-12K", "shotgun", True, True, "Titan quad rail + RK-1 grip, Atlas bipod, 10-round mag", {"mag": 16, "reserve": 164, "papMag": 24, "forceMag": True}),
    ("m32a1", "M32A1 (War Machine)", "launcher", True, True, "rubber buttpad, RK-1 grip, laser", {"mag": 6, "reserve": 36, "papName": "TBD", "papMag": 6, "papReserve": 54}),
    # ---- Wall buys (every log line after line 66; user, 2026-11-01). Prices to come from the user ----
    ("mosin", "Mosin", "sniper", True, True, "ProMag Archangel chassis + 10-round mag, carbine barrel, Bramit suppressor, Razor HD 1-6x", {"role": "wall"}),
    ("vityaz", "PP-19-01 Vityaz", "smg", True, True, "Zenit RK-3 grip, PT Lock + PT-1 stock, suppressor, Klassika rail + UTG, TangoDown grip", {"role": "wall"}),
    ("mdr", "MDR 7.62", "ar", True, True, "DD vertical foregrip, EOTech XPS3-0", {"role": "wall", "mag": 30, "papMag": 45, "forceMag": True}),  # 30, the classic mag (the user, 2026-11-10)
    ("ks23", "KS-23M", "shotgun", True, True, "700mm barrel, KOMZ Rusak reflex", {"role": "wall", "mag": 3}),
    ("m1a", "M1A", "sniper", True, True, "TROY SASS chassis, 30-round mag, Razor HD 1-6x, Harris bipod, Gemtech ONE suppressor", {"role": "wall", "mag": 8, "reserve": 96, "papMag": 12, "papName": "Mnesia", "forceMag": True, "fireModes": ["single"], "papFireModes": ["single"]}),  # the original's M14 (the user, 2026-11-08)
    ("uzi", "UZI", "smg", True, True, "100-round Beta C-Mag, suppressor, GL-CORE stock, StormWerkz mount + Pilad, foregrip", {"role": "wall"}),
    ("mp5k", "MP5K", "smg", True, True, "50-round X-5 drum, MFI mount + Walther MRS", {"role": "wall"}),
    ("mp133", "MP-133", "shotgun", True, True, "Salvo 12 suppressor, railed forestock + BCM grip, AGR-870 grip + DS150 stock, 8-shell mag, MRS",
     {"role": "wall", "mag": 5, "papMag": 8}),
    ("g36c", "G36C", "ar", True, True, "Slim Line stock, 318mm barrel + Warden, KAC Quad Rail + laser + Hera CQR grip, MRS",
     {"role": "wall", "fireModes": ["burst"]}),
    ("vsk94", "VSK-94", "ar", True, True, "Klassika rail + Walther MRS", {"role": "wall", "mag": 20, "papMag": 30}),
    ("raygun", "Ray Gun", "wonder", True, True, "its own X2 behaviour", {"mag": 20, "reserve": 160, "papName": "Porter's X2 Ray Gun", "papMag": 40, "papReserve": 200}),
    ("thundergun", "Thundergun", "wonder", True, True, "its own 4-charge version", {"mag": 2, "reserve": 12, "papName": "Zeus Cannon", "papMag": 4, "papReserve": 24}),
]

out = []
for gid, name, cls, base, pap, adds, extra in GUNS:
    mag = extra.get("mag", CLASS_MAG.get(cls))
    reserve = extra.get("reserve", mag * CLASS_RESERVE.get(cls, RESERVE))
    if cls == "shotgun" and "reserve" not in extra:
        reserve = max(reserve, SHOTGUN_MIN_RESERVE)
    pmag = extra.get("papMag", pap_mag(cls, mag))
    out.append({
        "id": gid, "name": name, "class": cls,
        "role": extra.get("role", "box_wonder" if cls == "wonder" else "box"),
        "wallPrice": extra.get("wallPrice"),
        "mag": mag, "reserve": reserve,
        # The mag in the gun (and its spare) made to hold "mag" (PaP'd: pap.mag) whatever mag is fitted
        "forceMag": extra.get("forceMag", False),
        "fireModes": extra.get("fireModes"),
        "baseLogged": base,
        "pap": {
            "name": extra.get("papName", "TBD"),
            "logged": pap,
            "adds": adds,
            "fireModes": extra.get("papFireModes", extra.get("fireModes")),
            "ubgl": extra.get("papUbgl"),
            "mag": pmag,
            # Never less than the base: at least 1.5x its reserve
            "reserve": extra.get("papReserve", max(pmag * CLASS_RESERVE.get(cls, PAP_RESERVE), int(reserve * 1.5))),
        },
    })

doc = {
    "_about": "Zombies weapon table (generated by make_guns.py; edit that, not this). All box guns except the M1911 "
              "starter. Builds are the logged lines in gun_library_raw.jsonl (newest per gun wins; the M4A1 and M16 "
              "share a template: the carry-handle build is the M16). Rules: ARs/SMGs mag 30, snipers 10, LMGs 75 "
              "whatever mag is fitted; M1911 8 -> 16 PaP'd. Every laser on a PaP'd gun is ON and drawn purple (115). "
              "M16 PaP = the base build + M203. Glock 18C single fire until PaP'd (then full auto); SA58 locked to single fire, PaP'd too. G36C burst only (PaP'd too). MDR 30 (PaP 45, the user 2026-11-10); VSK-94 keeps its real 20 (PaP 30): balanced by damage per bullet, not mag size. Wall prices come with the points system. SA58 mag 20 (PaP 30); MP5 always full auto. PaP mags: ARs 45, SMGs 50, LMGs 100, snipers 15, pistols double, shotguns +50% (own overrides: M1911 16, Glock 33, MP-43 2, Saiga 10, War Machine 6). Reserve 4x mag, PaP'd 5x. Damage per gun to be tuned.",
    "guns": out,
}
with open(os.path.join(HERE, "guns.json"), "w", encoding="utf-8") as f:
    json.dump(doc, f, indent=2, ensure_ascii=False)
print(len(out), "guns")

# ---------- box_builds.json: the logged builds the plugin builds display models from ----------

# The logged weapon name each gun's lines carry (the M4A1 and M16 share one: the M16 is the build with the carry handle)
LOGGED_NAME = {
    "m1911": "Colt M1911A1", "m4a1": "Colt M4A1", "m16": "Colt M4A1", "aug": "Steyr AUG A1", "ak74n": "AK-74N",
    "aks74u": "AKS-74U", "sa58": "SA58", "scarl": "SCAR-L", "mp7": "MP7A1", "ppsh": "PPSh-41", "sr2m": "SR-2M",
    "mp5": "HK MP5 9x19 submachine gun", "ump45": "UMP .45", "vector": "Vector Gen.2 9x19", "sv98": "SV-98",
    "m700": "Model 700", "sr25": "SR-25", "svds": "SVDS", "rpd": "RPD", "m60": "M60E6", "fiveseven": "Five-seveN",
    "glock18c": "Glock 18C", "deagle": "Desert Eagle", "rsh12": "RSh-12", "mp43": "MP-43-1C", "m870": "Model 870",
    "saiga12": "Saiga-12K", "m32a1": "M32A1", "raygun": "Ray Gun", "thundergun": "Thundergun",
    "mosin": "Mosin", "vityaz": "Vityaz", "mdr": "MDR", "ks23": "KS-23M", "m1a": "M1A", "uzi": "UZI",
    "mp5k": "MP5K", "mp133": "MP-133", "g36c": "HK G36", "vsk94": "VSK-94",
}
BOX_LINES = 66  # lines after this are wall buys (user, 2026-11-01)


def has_part(parts, name):
    return any(name in p["name"] or has_part(p.get("parts", []), name) for p in parts)


lines = [json.loads(l) for l in open(os.path.join(HERE, "gun_library_raw.jsonl"), encoding="utf-8") if l.strip()]
builds = []
for g in out:
    want = LOGGED_NAME[g["id"]]
    wall = g["role"] == "wall"
    pick = {}
    for i, l in enumerate(lines):
        # A line can say which it is ("role": "box" for a box gun added after the wall buys, e.g. the rebuilt M4A1)
        is_wall = l.get("role") == "wall" or (i >= BOX_LINES and l.get("role") != "box")
        if want not in l["name"] or is_wall != wall:
            continue
        if g["id"] in ("m4a1", "m16") and has_part(l["parts"], "carry handle") != (g["id"] == "m16"):
            continue
        if g["id"] == "mp5" and "MP5K" in l["name"]:
            continue
        pick[l["kind"]] = l  # newest wins
    if "base" not in pick:
        print("no base build for", g["id"])
        continue
    builds.append({
        "id": g["id"], "name": g["name"], "class": g["class"], "role": g["role"],
        "base": {"tpl": pick["base"]["tpl"], "parts": pick["base"]["parts"]},
        # The round the gun was logged with (any of either version's), for filling its mag
        "ammo": pick["base"].get("ammoTpl") or (pick["pap"].get("ammoTpl") if "pap" in pick else "") or "",
        "pap": {"tpl": pick["pap"]["tpl"], "parts": pick["pap"]["parts"]} if "pap" in pick else None,
    })
with open(os.path.join(HERE, "box_builds.json"), "w", encoding="utf-8") as f:
    json.dump(builds, f, indent=1, ensure_ascii=False)
print(len(builds), "builds,", sum(1 for b in builds if b["role"] != "wall"), "box/starter")

"""Retargets the original zombie traversal clips (GrayWolf .cast: window climbs, mantles, jump-downs) onto the game's
body skeleton and writes data/zombie_traverse.json in the same bone layout as data/zombie_anims.json, plus root motion.

How (no Unity needed): the source clip is played on the source zombie skeleton (forward kinematics), and each target
bone is turned so the limb it starts points where the source limb points, with its roll taken from the same landmark
on both skeletons (the knee/elbow bend plane, the palm across, the shin for the foot, the shoulders for the spine).
Matching where limbs point, not copying bone rotations, keeps knees and feet facing the way they do in the source
whatever the two skeletons' rest poses and bone axes are (the animation pack's bake turned feet 30-125 deg).

Source: Z up, X forward, Y left, centimetres, right-handed. Target / output: Unity (X right, Y up, Z forward), metres.
Only source POSITIONS are carried across (rotations are rebuilt from them), so the handedness change is just the
position mapping (x, y, z) -> (-y, z, x).

Usage: python -I retarget_traversal.py [--shots] [clip ...]   (no clips: the default list)
"""
import json, math, os, sys

sys.path.insert(0, r"C:\Git\99_Tools")
from cast_dump import load  # noqa: E402

ANIMS = os.path.join(os.environ.get("KZ_GRAYWOLF_ROOT", ""), "anims")
SOURCE_SKELETON = os.path.join(os.environ.get("KZ_GRAYWOLF_ROOT", ""), "models", "c_zom_der_zombie_body1", "c_zom_der_zombie_body1_LOD0.cast")
HERE = os.path.dirname(os.path.abspath(__file__))
TARGET_SKELETON = os.path.join(HERE, "..", "UnityAssets", "Zombies", "Anims", "eft_skeleton.json")
BAKED = os.path.join(HERE, "..", "data", "zombie_anims.json")
OUT = os.path.join(HERE, "..", "data", "zombie_traverse.json")
SHOTS = os.path.join(HERE, "..", "logs", "traverse_shots")

DEFAULT_CLIPS = [
    "bo3_ai_base_zombie_traverse_v6",
    "ai_zombie_base_mantle_over_36", "ai_zombie_base_mantle_over_48", "ai_zombie_base_mantle_over_72", "ai_zombie_base_mantle_over_96",
    "ai_zombie_base_run_mantle_over_36", "ai_zombie_base_run_mantle_over_48",
    "ai_zombie_base_crawl_mantle_over_36", "ai_zombie_base_crawl_mantle_over_48",
    "ai_zombie_base_jump_down_36", "ai_zombie_base_jump_down_48", "ai_zombie_base_jump_down_72", "ai_zombie_base_jump_down_96",
    "ai_zombie_base_jump_down_128", "ai_zombie_base_jump_down_160",
    "ai_zombie_base_run_jump_down_36", "ai_zombie_base_run_jump_down_48", "ai_zombie_base_run_jump_down_72", "ai_zombie_base_run_jump_down_96",
    # (ai_zm_dlc5_theater_zombie_jump_down_stumble_286: an 8 m drop on a later map's rig, comes out wrong: left out)
    "ai_zombie_base_crawl_jump_down_36", "ai_zombie_base_crawl_jump_down_48", "ai_zombie_base_crawl_jump_down_72", "ai_zombie_base_crawl_jump_down_96",
    # Ledges up (user 2026-11-15: Map Kit ledge markers, zombies jump up to a player above)
    "ai_zombie_base_jump_up_36", "ai_zombie_base_jump_up_48", "ai_zombie_base_jump_up_72", "ai_zombie_base_jump_up_96",
    "ai_zombie_base_jump_up_128", "ai_zombie_base_jump_up_160",
    "ai_zombie_base_run_jump_up_36", "ai_zombie_base_run_jump_up_48", "ai_zombie_base_run_jump_up_72", "ai_zombie_base_run_jump_up_96",
    "ai_zombie_base_crawl_jump_up_36", "ai_zombie_base_crawl_jump_up_48", "ai_zombie_base_crawl_jump_up_72", "ai_zombie_base_crawl_jump_up_96",
    "ai_zombie_base_crawl_jump_up_128", "ai_zombie_base_crawl_jump_up_160",
    # Shocked by the Wunderwaffe DG-2 (user 2026-11-16): stood up jerking, then down. Played in place (root unused)
    "ai_zm_dlc5_zombie_tesla_death_a", "ai_zm_dlc5_zombie_tesla_death_b", "ai_zm_dlc5_zombie_tesla_death_d",
    "ai_zm_dlc5_zombie_tesla_crawl_death_a", "ai_zm_dlc5_zombie_tesla_crawl_death_b",
]

# ---------------------------------------------------------------- maths (quaternions x, y, z, w; 3x3 as row lists)

def qmul(a, b):
    ax, ay, az, aw = a; bx, by, bz, bw = b
    return (aw*bx + ax*bw + ay*bz - az*by, aw*by - ax*bz + ay*bw + az*bx,
            aw*bz + ax*by - ay*bx + az*bw, aw*bw - ax*bx - ay*by - az*bz)

def qinv(q):
    return (-q[0], -q[1], -q[2], q[3])

def qrot(q, v):
    x, y, z, w = q; vx, vy, vz = v
    tx = 2*(y*vz - z*vy); ty = 2*(z*vx - x*vz); tz = 2*(x*vy - y*vx)
    return (vx + w*tx + (y*tz - z*ty), vy + w*ty + (z*tx - x*tz), vz + w*tz + (x*ty - y*tx))

def qnorm(q):
    n = math.sqrt(sum(c*c for c in q)) or 1.0
    return tuple(c / n for c in q)

def add(a, b): return (a[0]+b[0], a[1]+b[1], a[2]+b[2])
def sub(a, b): return (a[0]-b[0], a[1]-b[1], a[2]-b[2])
def mul(a, k): return (a[0]*k, a[1]*k, a[2]*k)
def dot(a, b): return a[0]*b[0] + a[1]*b[1] + a[2]*b[2]
def cross(a, b): return (a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0])
def length(a): return math.sqrt(dot(a, a))
def unit(a):
    n = length(a)
    return mul(a, 1.0 / n) if n > 1e-9 else (0.0, 0.0, 0.0)

def mat_from_q(q):
    x, y, z, w = q
    return [[1-2*(y*y+z*z), 2*(x*y-z*w), 2*(x*z+y*w)],
            [2*(x*y+z*w), 1-2*(x*x+z*z), 2*(y*z-x*w)],
            [2*(x*z-y*w), 2*(y*z+x*w), 1-2*(x*x+y*y)]]

def q_from_mat(m):
    t = m[0][0] + m[1][1] + m[2][2]
    if t > 0:
        s = math.sqrt(t + 1.0) * 2
        return qnorm(((m[2][1]-m[1][2])/s, (m[0][2]-m[2][0])/s, (m[1][0]-m[0][1])/s, 0.25*s))
    if m[0][0] > m[1][1] and m[0][0] > m[2][2]:
        s = math.sqrt(1.0 + m[0][0] - m[1][1] - m[2][2]) * 2
        return qnorm((0.25*s, (m[0][1]+m[1][0])/s, (m[0][2]+m[2][0])/s, (m[2][1]-m[1][2])/s))
    if m[1][1] > m[2][2]:
        s = math.sqrt(1.0 + m[1][1] - m[0][0] - m[2][2]) * 2
        return qnorm(((m[0][1]+m[1][0])/s, 0.25*s, (m[1][2]+m[2][1])/s, (m[0][2]-m[2][0])/s))
    s = math.sqrt(1.0 + m[2][2] - m[0][0] - m[1][1]) * 2
    return qnorm(((m[0][2]+m[2][0])/s, (m[1][2]+m[2][1])/s, 0.25*s, (m[1][0]-m[0][1])/s))

def frame(primary, secondary):
    """A rotation whose X axis is `primary` and whose Y axis is `secondary` made square to it."""
    d = unit(primary)
    s = unit(sub(secondary, mul(d, dot(secondary, d))))
    t = cross(d, s)
    return q_from_mat([[d[0], s[0], t[0]], [d[1], s[1], t[1]], [d[2], s[2], t[2]]])

def pole(a, b, c):
    """Which way the middle joint b sticks out from the line a-c (the bend's direction)."""
    ac = sub(c, a)
    k = dot(sub(b, a), ac) / max(1e-9, dot(ac, ac))
    return sub(b, add(a, mul(ac, k)))

# ---------------------------------------------------------------- source

def first(x):
    return x[0] if isinstance(x, list) else x

def find(n, kind):
    if n.kind == kind:
        return n
    for c in n.children:
        r = find(c, kind)
        if r:
            return r
    return None

def as_list(v):
    if v is None:
        return []
    return v if isinstance(v, list) else [v]

def load_source_skeleton():
    sk = find(first(load(SOURCE_SKELETON)), "skeleton")
    bones = []
    for b in sk.find("bone"):
        p = b.get("p")
        bones.append({"name": b.get("n"), "parent": None if p in (None, 0xFFFFFFFF, -1) else p,
                      "lp": tuple(b.get("lp")), "lr": tuple(b.get("lr"))})
    return bones

def load_clip(name, bones):
    an = find(first(load(os.path.join(ANIMS, name + ".cast"))), "animation")
    fps = an.get("fr") or 30.0
    idx = {b["name"]: i for i, b in enumerate(bones)}
    tracks = {}
    frames = 1
    for c in an.find("curve"):
        bone = c.get("nn")
        if bone not in idx:
            continue
        keys = [int(k) for k in as_list(c.get("kb"))]
        vals = as_list(c.get("kv"))
        prop = c.get("kp")
        if prop == "rq":
            # Either one (x, y, z, w) per key or a flat list of floats, four per key
            vals = [tuple(v) for v in vals] if vals and not isinstance(vals[0], (int, float)) \
                else [tuple(vals[i:i+4]) for i in range(0, len(vals), 4)]
        frames = max(frames, (keys[-1] + 1) if keys else 1)
        tracks[(bone, prop)] = (keys, vals, c.get("m"))
    return fps, frames, tracks

def sample(track, f):
    keys, vals, _ = track
    if not keys:
        return None
    if f <= keys[0]:
        return vals[0]
    if f >= keys[-1]:
        return vals[-1]
    for i in range(1, len(keys)):
        if keys[i] >= f:
            a, b = keys[i-1], keys[i]
            t = (f - a) / max(1, b - a)
            va, vb = vals[i-1], vals[i]
            if isinstance(va, tuple):
                if dot4(va, vb) < 0:
                    vb = tuple(-c for c in vb)
                return qnorm(tuple(x + (y - x) * t for x, y in zip(va, vb)))
            return va + (vb - va) * t
    return vals[-1]

def dot4(a, b): return sum(x*y for x, y in zip(a, b))

def source_pose(bones, tracks, f):
    """Every source bone's world position, mapped to Unity space in metres."""
    world = []
    for b in bones:
        lr = b["lr"]
        lp = list(b["lp"])
        r = tracks.get((b["name"], "rq"))
        if r:
            lr = sample(r, f)
        for i, ax in enumerate(("tx", "ty", "tz")):
            t = tracks.get((b["name"], ax))
            if t:
                v = sample(t, f)
                lp[i] = v if t[2] == "absolute" else lp[i] + v
        if b["parent"] is None:
            world.append((tuple(lp), lr))
        else:
            pp, pr = world[b["parent"]]
            world.append((add(pp, qrot(pr, tuple(lp))), qmul(pr, lr)))
    return {b["name"]: to_unity(world[i][0]) for i, b in enumerate(bones)}, {b["name"]: world[i][1] for i, b in enumerate(bones)}

def to_unity(p):
    return (-p[1] / 100.0, p[2] / 100.0, p[0] / 100.0)

# ---------------------------------------------------------------- target

def load_target():
    raw = json.load(open(TARGET_SKELETON))
    by_id = {k: v for k, v in raw.items()}
    order, seen = [], set()
    def visit(k):
        if k in seen:
            return
        p = str(by_id[k]["parent"])
        if p in by_id:
            visit(p)
        seen.add(k)
        order.append(k)
    for k in by_id:
        visit(k)
    bones = []
    index = {}
    for k in order:
        v = by_id[k]
        p = str(v["parent"])
        index[k] = len(bones)
        bones.append({"name": v["name"], "parent": index[p] if p in index else None,
                      "lp": tuple(v["pos"]), "lr": tuple(v["rot"])})
    return bones

def target_rest(bones):
    pos, rot = [], []
    for b in bones:
        if b["parent"] is None:
            pos.append(b["lp"]); rot.append(b["lr"])
        else:
            pp, pr = pos[b["parent"]], rot[b["parent"]]
            pos.append(add(pp, qrot(pr, b["lp"]))); rot.append(qmul(pr, b["lr"]))
    return pos, rot

# ---------------------------------------------------------------- the mapping

H = "Base Human"

def tgt_frames(T, R):
    """Each mapped target bone's landmark frame in a pose (T positions / R rotations by name)."""
    right = sub(T[H+"RCollarbone"], T[H+"LCollarbone"])
    up_chest = sub(T[H+"Neck"], T[H+"Spine3"])
    f = {}
    f[H+"Pelvis"] = frame(sub(T[H+"Spine1"], T[H+"Pelvis"]), sub(T[H+"RThigh1"], T[H+"LThigh1"]))
    f[H+"Spine1"] = frame(sub(T[H+"Spine2"], T[H+"Spine1"]), right)
    f[H+"Spine2"] = frame(sub(T[H+"Spine3"], T[H+"Spine2"]), right)
    f[H+"Spine3"] = frame(sub(T[H+"Neck"], T[H+"Spine3"]), right)
    f[H+"Neck"] = frame(sub(T[H+"Head"], T[H+"Neck"]), right)
    # The head has no child joint: its own axes (forward +Y, up -X: the eyes' axes, ZombieEyes)
    f[H+"Head"] = frame(qrot(R[H+"Head"], (-1, 0, 0)), qrot(R[H+"Head"], (0, 1, 0)))
    for s in "LR":
        f[H+s+"Collarbone"] = frame(sub(T[H+s+"Upperarm"], T[H+s+"Collarbone"]), up_chest)
        arm_n = cross(sub(T[H+s+"Forearm1"], T[H+s+"Upperarm"]), sub(T[H+s+"Palm"], T[H+s+"Forearm1"]))
        f[H+s+"Upperarm"] = frame(sub(T[H+s+"Forearm1"], T[H+s+"Upperarm"]), arm_n)
        f[H+s+"Forearm1"] = frame(sub(T[H+s+"Palm"], T[H+s+"Forearm1"]), arm_n)
        f[H+s+"Palm"] = frame(sub(T[H+s+"Digit31"], T[H+s+"Palm"]), sub(T[H+s+"Digit21"], T[H+s+"Digit51"]))
        leg_n = cross(sub(T[H+s+"Calf"], T[H+s+"Thigh1"]), sub(T[H+s+"Foot"], T[H+s+"Calf"]))
        f[H+s+"Thigh1"] = frame(sub(T[H+s+"Calf"], T[H+s+"Thigh1"]), leg_n)
        f[H+s+"Calf"] = frame(sub(T[H+s+"Foot"], T[H+s+"Calf"]), leg_n)
        f[H+s+"Foot"] = frame(sub(T[H+s+"Toe"], T[H+s+"Foot"]), sub(T[H+s+"Calf"], T[H+s+"Foot"]))
    return f

def src_frames(P, Q, prev_n):
    """The same landmarks on the source pose (P positions by source bone name). prev_n keeps bend planes steady when
    a limb is straight (its bend plane is undefined there)."""
    side = {"L": "le", "R": "ri"}
    right = sub(P["j_clavicle_ri"], P["j_clavicle_le"])
    up_chest = sub(P["j_neck"], P["j_spine4"])
    f = {}
    f[H+"Pelvis"] = frame(sub(P["j_spinelower"], P["j_mainroot"]), sub(P["j_hip_ri"], P["j_hip_le"]))
    f[H+"Spine1"] = frame(sub(P["j_spineupper"], P["j_spinelower"]), right)
    f[H+"Spine2"] = frame(sub(P["j_spine4"], P["j_spineupper"]), right)
    f[H+"Spine3"] = frame(sub(P["j_neck"], P["j_spine4"]), right)
    f[H+"Neck"] = frame(sub(P["j_head"], P["j_neck"]), right)
    f[H+"Head"] = frame(sub(P["j_helmet"], P["j_head"]), sub(P["tag_eye"], P["j_head"]))
    for s, x in side.items():
        f[H+s+"Collarbone"] = frame(sub(P["j_shoulder_"+x], P["j_clavicle_"+x]), up_chest)
        upper = sub(P["j_elbow_"+x], P["j_shoulder_"+x]); lower = sub(P["j_wrist_"+x], P["j_elbow_"+x])
        arm_n = steady(cross(upper, lower), prev_n, "arm"+s, upper)
        f[H+s+"Upperarm"] = frame(upper, arm_n)
        f[H+s+"Forearm1"] = frame(lower, arm_n)
        f[H+s+"Palm"] = frame(sub(P["j_mid_"+x+"_1"], P["j_wrist_"+x]), sub(P["j_index_"+x+"_1"], P["j_pinkybase_"+x]))
        thigh = sub(P["j_knee_"+x], P["j_hip_"+x]); shin = sub(P["j_ankle_"+x], P["j_knee_"+x])
        leg_n = steady(cross(thigh, shin), prev_n, "leg"+s, thigh)
        f[H+s+"Thigh1"] = frame(thigh, leg_n)
        f[H+s+"Calf"] = frame(shin, leg_n)
        f[H+s+"Foot"] = frame(sub(P["j_ball_"+x], P["j_ankle_"+x]), sub(P["j_knee_"+x], P["j_ankle_"+x]))
    return f

def steady(n, prev, key, along):
    """A bend plane's normal, kept from the frame before when the limb is nearly straight (and never flipped)."""
    old = prev.get(key)
    if length(n) < 0.002 * max(1e-6, length(along)) ** 2 * 10 and old is not None:
        n = old
    elif old is not None and dot(n, old) < 0 and length(n) < 0.05 * dot(along, along):
        n = mul(n, -1)
    prev[key] = n
    return n

# The source's straight-legged neutral for a limb whose first frame is straight: the rest pose's bend planes
def rest_normals(src_bones):
    P, Q = source_pose(src_bones, {}, 0)
    prev = {}
    for x, s in (("le", "L"), ("ri", "R")):
        prev["arm"+s] = cross(sub(P["j_elbow_"+x], P["j_shoulder_"+x]), sub(P["j_wrist_"+x], P["j_elbow_"+x]))
        prev["leg"+s] = cross(sub(P["j_knee_"+x], P["j_hip_"+x]), sub(P["j_ankle_"+x], P["j_knee_"+x]))
    return prev

# ---------------------------------------------------------------- retarget one clip

def retarget(name, src_bones, tgt_bones, rest_pos, rest_rot, recorded):
    fps, frames, tracks = load_clip(name, src_bones)
    names = [b["name"] for b in tgt_bones]
    T = dict(zip(names, rest_pos)); R = dict(zip(names, rest_rot))
    tf = tgt_frames(T, R)
    # Each mapped bone's rotation relative to its landmark frame, from the rest pose (constant)
    offset = {n: qmul(qinv(tf[n]), R[n]) for n in tf}
    root_i = names.index("Root_Joint")
    pelvis_i = names.index(H+"Pelvis")
    # Size: the target's leg (hip to ankle) over the source's
    src_rest_P, _ = source_pose(src_bones, {}, 0)
    leg_t = length(sub(T[H+"LCalf"], T[H+"LThigh1"])) + length(sub(T[H+"LFoot"], T[H+"LCalf"]))
    leg_s = length(sub(src_rest_P["j_knee_le"], src_rest_P["j_hip_le"])) + length(sub(src_rest_P["j_ankle_le"], src_rest_P["j_knee_le"]))
    scale = leg_t / leg_s
    prev = rest_normals(src_bones)
    rot_rows, hips_rows, root_rows, poses = [], [], [], []
    for f in range(frames):
        P, Q = source_pose(src_bones, tracks, f)
        sf = src_frames(P, Q, prev)
        origin = P["tag_origin"]
        # The root's yaw (tag_origin turns about up): kept as root motion, the body posed facing the root's way
        oq = Q["tag_origin"]
        fwd = to_unity_dir(qrot(oq, (1, 0, 0)))
        yaw = math.degrees(math.atan2(fwd[0], fwd[2]))
        unturn = yaw_q(-yaw)
        root_rows.append([round(origin[0] * scale, 4), round(origin[1] * scale, 4), round(origin[2] * scale, 4), round(yaw, 3)])
        # World pose of the target, root at the origin facing +Z
        wpos = [None] * len(tgt_bones); wrot = [None] * len(tgt_bones)
        for i, b in enumerate(tgt_bones):
            n = b["name"]
            if b["parent"] is None:
                wpos[i], wrot[i] = b["lp"], b["lr"]
                continue
            ppos, prot = wpos[b["parent"]], wrot[b["parent"]]
            if i == pelvis_i:
                hip = mul(qrot(unturn, sub(P["j_mainroot"], origin)), scale)
                # The source hip joint sits a touch off the target's pelvis: matched at the rest pose
                wpos[i] = add(hip, pelvis_shift(src_rest_P, T, scale))
            else:
                wpos[i] = add(ppos, qrot(prot, b["lp"]))
            if n in sf:
                wrot[i] = qnorm(qmul(qmul(yaw_q(-yaw), sf[n]), offset[n]))
            else:
                wrot[i] = qmul(prot, b["lr"])
        row = []
        for n in recorded:
            i = names.index(n)
            p = tgt_bones[i]["parent"]
            local = qmul(qinv(wrot[p]), wrot[i]) if p is not None else wrot[i]
            row.extend(round(c, 5) for c in local)
        rot_rows.append(row)
        # The pelvis in the root joint's own space (what the plugin sets as its local position)
        rel = qrot(qinv(wrot[root_i]), sub(wpos[pelvis_i], wpos[root_i]))
        hips_rows.append([round(c, 4) for c in rel])
        poses.append((P, dict(zip(names, wpos))))
    # Root motion relative to the first frame
    r0 = root_rows[0]
    root_rows = [[r[0]-r0[0], r[1]-r0[1], r[2]-r0[2], r[3]-r0[3]] for r in root_rows]
    # How high its legs clear (the highest the lowest knee/foot/toe gets, root motion included): the sill the original
    # was made for. The plugin lowers the lift to the real sill (ZombiesTraverse: vaulted far too high over 0.9 m sandbags)
    legs = [n for n in names if any(k in n for k in ("Calf", "Foot", "Toe"))]
    clear = max(min(W[n][1] for n in legs) + root_rows[f][1] for f, (_, W) in enumerate(poses))
    clip = {"name": short(name), "length": round((frames - 1) / fps, 4), "frames": frames, "loop": False, "speed": 0,
            "clear": round(clear, 3), "rot": rot_rows, "hips": hips_rows, "root": root_rows}
    return clip, poses, fps

def pelvis_shift(src_P, T, scale):
    return (0.0, 0.0, 0.0)

def to_unity_dir(v):
    return (-v[1], v[2], v[0])

def yaw_q(deg):
    h = math.radians(deg) / 2
    return (0.0, math.sin(h), 0.0, math.cos(h))

def short(name):
    return name.replace("ai_zombie_base_", "").replace("bo3_ai_base_zombie_", "").replace("ai_zm_dlc5_theater_zombie_", "")         .replace("ai_zm_dlc5_zombie_", "")

# ---------------------------------------------------------------- checks

LIMBS = [("thigh", "j_hip_{x}", "j_knee_{x}", "{s}Thigh1", "{s}Calf"), ("shin", "j_knee_{x}", "j_ankle_{x}", "{s}Calf", "{s}Foot"),
         ("foot", "j_ankle_{x}", "j_ball_{x}", "{s}Foot", "{s}Toe"), ("upper arm", "j_shoulder_{x}", "j_elbow_{x}", "{s}Upperarm", "{s}Forearm1"),
         ("forearm", "j_elbow_{x}", "j_wrist_{x}", "{s}Forearm1", "{s}Palm"), ("spine", "j_spinelower", "j_neck", "Spine1", "Neck")]

def check(clip, poses):
    """How far each output limb points from the same limb on the source, worst over the clip (degrees), plus the
    knees' bend direction (the bend plane: what twisted the pack's legs)."""
    worst = {}
    for P, W in poses:
        for name, sa, sb, ta, tb in LIMBS:
            for x, s in (("le", "L"), ("ri", "R")):
                a = sub(P[sb.format(x=x)], P[sa.format(x=x)])
                b = sub(W[H + tb.format(s=s)], W[H + ta.format(s=s)])
                if length(a) < 1e-6 or length(b) < 1e-6:
                    continue
                ang = math.degrees(math.acos(max(-1.0, min(1.0, dot(unit(a), unit(b))))))
                worst[name] = max(worst.get(name, 0.0), ang)
            for x, s in (("le", "L"), ("ri", "R")):
                ps = pole(P["j_hip_"+x], P["j_knee_"+x], P["j_ankle_"+x])
                pt = pole(W[H+s+"Thigh1"], W[H+s+"Calf"], W[H+s+"Foot"])
                if length(ps) > 0.03 and length(pt) > 0.03:
                    ang = math.degrees(math.acos(max(-1.0, min(1.0, dot(unit(ps), unit(pt))))))
                    worst["knee bend"] = max(worst.get("knee bend", 0.0), ang)
    return worst

def draw(clip_name, poses, fps, picks):
    try:
        from PIL import Image, ImageDraw
    except ImportError:
        return
    os.makedirs(SHOTS, exist_ok=True)
    src_links = [("j_mainroot", "j_spinelower"), ("j_spinelower", "j_spineupper"), ("j_spineupper", "j_spine4"), ("j_spine4", "j_neck"),
                 ("j_neck", "j_head"), ("j_head", "j_helmet")]
    for x in ("le", "ri"):
        src_links += [("j_spine4", "j_clavicle_"+x), ("j_clavicle_"+x, "j_shoulder_"+x), ("j_shoulder_"+x, "j_elbow_"+x), ("j_elbow_"+x, "j_wrist_"+x),
                      ("j_mainroot", "j_hip_"+x), ("j_hip_"+x, "j_knee_"+x), ("j_knee_"+x, "j_ankle_"+x), ("j_ankle_"+x, "j_ball_"+x)]
    tgt_links = [(H+"Pelvis", H+"Spine1"), (H+"Spine1", H+"Spine2"), (H+"Spine2", H+"Spine3"), (H+"Spine3", H+"Neck"), (H+"Neck", H+"Head")]
    for s in "LR":
        tgt_links += [(H+"Spine3", H+s+"Collarbone"), (H+s+"Collarbone", H+s+"Upperarm"), (H+s+"Upperarm", H+s+"Forearm1"), (H+s+"Forearm1", H+s+"Palm"),
                      (H+"Pelvis", H+s+"Thigh1"), (H+s+"Thigh1", H+s+"Calf"), (H+s+"Calf", H+s+"Foot"), (H+s+"Foot", H+s+"Toe")]
    cell, k = 260, 95.0
    img = Image.new("RGB", (cell * 4, cell * len(picks)), (24, 24, 28))
    g = ImageDraw.Draw(img)
    for row, f in enumerate(picks):
        P, W = poses[f]
        o = P["tag_origin"]
        views = [("side src", P, src_links, lambda p: (p[2]-o[2], p[1]-o[1]), (90, 200, 255)),
                 ("side out", W, tgt_links, lambda p: (p[2], p[1]), (255, 170, 80)),
                 ("front src", P, src_links, lambda p: (p[0]-o[0], p[1]-o[1]), (90, 200, 255)),
                 ("front out", W, tgt_links, lambda p: (p[0], p[1]), (255, 170, 80))]
        for col, (label, pts, links, proj, colour) in enumerate(views):
            ox, oy = col * cell + cell / 2, row * cell + cell - 30
            g.line([(col*cell+10, oy), (col*cell+cell-10, oy)], fill=(60, 60, 70))
            for a, b in links:
                if a in pts and b in pts:
                    pa, pb = proj(pts[a]), proj(pts[b])
                    left = ("_le" in a or "_le" in b or H+"L" in a or H+"L" in b)
                    c = colour if not left else tuple(int(v * 0.55) for v in colour)
                    g.line([(ox + pa[0]*k, oy - pa[1]*k), (ox + pb[0]*k, oy - pb[1]*k)], fill=c, width=3)
            g.text((col*cell + 8, row*cell + 6), f"{label}  f{f} ({f/fps:.2f}s)", fill=(200, 200, 200))
    img.save(os.path.join(SHOTS, short(clip_name) + ".png"))

# ---------------------------------------------------------------- main

def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    clips = args or DEFAULT_CLIPS
    src_bones = load_source_skeleton()
    tgt_bones = load_target()
    rest_pos, rest_rot = target_rest(tgt_bones)
    baked = json.load(open(BAKED))
    recorded, parents = baked["bones"], baked["parents"]
    out = {"bones": recorded, "parents": parents, "fps": 30, "clips": []}
    for name in clips:
        if not os.path.exists(os.path.join(ANIMS, name + ".cast")):
            print(f"{name}: missing, skipped")
            continue
        clip, poses, fps = retarget(name, src_bones, tgt_bones, rest_pos, rest_rot, recorded)
        worst = check(clip, poses)
        n = len(poses)
        draw(name, poses, fps, sorted(set([0, n // 4, n // 2, 3 * n // 4, n - 1])))
        r = clip["root"][-1]
        print(f"{clip['name']:28} {clip['length']:5.2f} s  root fwd {r[2]:5.2f} m, up {r[1]:5.2f} m, side {r[0]:5.2f} m, turn {r[3]:5.1f}"
              f"  | worst limb off source: " + ", ".join(f"{k} {v:.0f}" for k, v in worst.items()))
        out["clips"].append(clip)
    with open(OUT, "w") as fh:
        json.dump(out, fh, separators=(",", ":"))
    print("wrote", OUT, os.path.getsize(OUT) // 1024, "KB")

if __name__ == "__main__":
    main()

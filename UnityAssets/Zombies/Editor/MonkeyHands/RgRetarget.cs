using System;
using System.Collections.Generic;
using System.Linq;
using ZombiesMonkeyHands.ModelFormats;
using UnityEditor;
using UnityEngine;

namespace ZombiesMonkeyHands.Editor
{
    /// <summary>
    /// Turns the original game viewmodel animations into clips for EFT's weapon rig (ported from the Ray Gun, where "the original" and "the
    /// Saiga" below mean the the original game animation and the vanilla EFT rig it's anchored to; here the rig is the beer bottle's).
    ///
    /// The gun and its moving parts take the original's poses directly. The original view space is anchored to the rig by one
    /// translation: the right wrist in the first idle frame lands on the Saiga's right palm (the original holds its viewmodel
    /// lower and further out, for a narrower FOV). The camera follows tag_camera's motion relative to idle.
    ///
    /// EFT's arms are a different skeleton, so they're solved, not copied: each palm goes where the original's wrist is, the
    /// elbow is placed by two-bone IK with the original's elbow as the pole, and forearm twist is shared out over the two
    /// twist bones. Orientations are matched through frames built from each rig's own joint positions (wrist to
    /// middle knuckle, index to pinky knuckle; each finger joint to the next), so neither rig's bone axis
    /// conventions matter.
    /// </summary>
    public class RgRetarget
    {
        private readonly Transform _root;
        private readonly Dictionary<string, Transform> _rig;
        private readonly SrcModel _gun;
        private readonly Dictionary<string, Transform> _gunBones;
        private readonly Quaternion _jgunLocal;
        private readonly SrcModel _bind; // The original's arms+gun animation rig (am_t5_thundergun), for bind-relative joints

        private readonly Dictionary<string, Vector3> _restPos = new Dictionary<string, Vector3>();
        private readonly Dictionary<string, Quaternion> _restRot = new Dictionary<string, Quaternion>();
        private readonly Dictionary<string, Quaternion> _restLocal = new Dictionary<string, Quaternion>();
        private readonly Dictionary<string, Vector3> _restLocalPos = new Dictionary<string, Vector3>();

        private Vector3 _offset;      // The original view space -> prefab space translation
        private SrcPose _cameraRef;   // tag_camera at idle
        private readonly SrcAnim _idle; // poses for parts a partial animation doesn't key

        private readonly Side[] _sides;

        public Vector3 Offset => _offset;

        private class Finger
        {
            public string[] Eft = new string[3];
            public string[] Src = new string[3];
            public Quaternion[] Qe = new Quaternion[2];  // EFT joint 1-2: bone rotation -> anatomical frame
            public Quaternion Bind23;                     // The original joint 2 -> 3 relative rotation at bind
        }

        private class Side
        {
            public string E, B; // "L"/"R", "le"/"ri"
            public string Collar, Upper, Fore1, Fore2, Fore3, Palm;
            public float L1, L2;
            public Quaternion QUpper, QFore1, QHand;
            public Vector3 RestPole;
            public Vector3 Shift; // shoulder moved forward to reach the original's grips
            public Finger[] Fingers;
        }

        // EFT Digit1..5 = thumb, index, middle, ring, pinky
        private static readonly string[] BoFingers = { "thumb", "index", "mid", "ring", "pinky" };

        public RgRetarget(Transform root, Dictionary<string, Transform> rig, SrcModel gun, Dictionary<string, Transform> gunBones,
            Quaternion jgunLocal, SrcModel bind, SrcAnim idle)
        {
            _root = root;
            _rig = rig;
            _gun = gun;
            _gunBones = gunBones;
            _jgunLocal = jgunLocal;
            _bind = bind;
            _idle = idle;

            foreach (var (name, t) in rig)
            {
                _restPos[name] = _root.InverseTransformPoint(t.position);
                _restRot[name] = Quaternion.Inverse(_root.rotation) * t.rotation;
                _restLocal[name] = t.localRotation;
                _restLocalPos[name] = t.localPosition;
            }

            _sides = new[] { MakeSide("L", "le"), MakeSide("R", "ri") };

            // Anchor, per axis, between the original's own placement (its eye on EFT's eye) and the Saiga's (the original's right wrist
            // on the Saiga's right palm). The original carries the gun at the hip, further out than EFT's arms reach: take the
            // Saiga's depth so the front grip is reachable, but keep most of the original's low carry so the back of the gun
            // stays clear of the camera.
            SrcPose[] idle0 = idle.Frames[0];
            _cameraRef = idle0[idle.PartIndex("tag_camera")];
            Vector3 eyeAnchor = _restPos["Camera_animated"] - RgSpace.Pos(_cameraRef.Pos);
            Vector3 palmAnchor = _restPos["Base HumanRPalm"] - RgSpace.Pos(idle0[idle.PartIndex("j_wrist_ri")].Pos);
            _offset = eyeAnchor + Vector3.Scale(palmAnchor - eyeAnchor, AnchorBlend) + ViewNudge;

            // Whatever reach is still missing at idle comes from moving the shoulders forward a little
            foreach (Side s in _sides)
            {
                Vector3 w = RgSpace.Pos(idle0[idle.PartIndex($"j_wrist_{s.B}")].Pos) + _offset;
                Vector3 d = w - _restPos[s.Upper];
                float missing = d.magnitude - (s.L1 + s.L2 - ReachMargin);
                s.Shift = missing > 0f ? d.normalized * Mathf.Min(missing, MaxShoulderShift) : Vector3.zero;
            }
        }

        /// <summary>How far (0 = the original's placement, 1 = the Saiga's) the gun moves on each prefab axis: right, up, forward.</summary>
        public static Vector3 AnchorBlend = new Vector3(0.5f, 0.2f, 1f);
        /// <summary>Hand-tuned in game: further out and a little up, framed like the original/the original's view of the gun.</summary>
        public static Vector3 ViewNudge = new Vector3(0f, 0.01f, 0.14f);
        /// <summary>The item alone moved off the hands (prefab space: right, up, forward), metres.</summary>
        public static Vector3 GunShift = Vector3.zero;
        private const float ReachMargin = 0.02f;

        // A shot's visible kick (when a clip asks for one): straight back into the shoulder and settling, instead of the original's upward rock
        private const float KickBack = 0.06f, KickPeak = 0.04f, KickSettle = 0.25f;
        private const float CameraKickDegrees = 0.7f, CameraKickBack = 0.012f;

        // The blast's rumble: a decaying shudder (roll) through the gun and, a little, the view. 12 Hz stays under
        // the clip's 30 fps sampling (faster would alias into a slow wobble)
        private const float GunRumbleDegrees = 1.2f, CameraRumbleDegrees = 0.35f, RumbleHz = 12f, RumbleDecay = 7f;

        private static float Rumble(float t) => Mathf.Sin(2f * Mathf.PI * RumbleHz * t) * Mathf.Exp(-RumbleDecay * t);

        private static float KickCurve(float t)
        {
            float k = t < KickPeak ? t / KickPeak : 1f - (t - KickPeak) / KickSettle;
            k = Mathf.Clamp01(k);
            return k * k * (3f - 2f * k);
        }
        public static float MaxShoulderShift = 0.2f; // the gun sits well out: the left hand needs the reach (the monkey sets 0)

        // The side dial's knob, and how far (metres) the left hand is kept off it at most
        private const string KnobTag = "tag_knob_animate";
        private const float KnobClearance = 0.02f;

        /// <summary>
        /// The built inspect, 0..1 through it: the gun is held out a little and turned in the hand to show its left
        /// side (the dial), held there, then smoothly put back. The original has no inspect for the Ray Gun.
        /// Returns the turn (prefab space, about the right wrist) and the move.
        /// </summary>
        public static (Quaternion rot, Vector3 move) InspectPose(float t)
        {
            float Ease(float a, float b) => Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(a, b, t));
            // 0 at rest, 1 turned and held out for looking at
            float up = Ease(0f, 0.3f) * (1f - Ease(0.72f, 1f));
            float side = up;
            Quaternion rot = Quaternion.AngleAxis(-InspectYaw * side, Vector3.up)
                             * Quaternion.AngleAxis(InspectRoll * side, Vector3.forward)
                             * Quaternion.AngleAxis(-InspectPitch * up, Vector3.right);
            return (rot, InspectMove * up);
        }

        private const float InspectYaw = 50f, InspectRoll = 15f, InspectPitch = 8f;
        private static readonly Vector3 InspectMove = new Vector3(-0.03f, 0.02f, 0.02f);

        /// <summary>
        /// Per-frame changes on top of an animation: the whole viewmodel moved (<see cref="Push"/>, prefab space), the
        /// left hand and anything it holds (<see cref="LeftGroup"/>) turned about the left wrist and moved, and gun parts
        /// kept at their rest pose whatever the animation does with them (<see cref="Hold"/>).
        /// </summary>
        public class Extras
        {
            public Vector3 Push;
            public Quaternion LeftTwist = Quaternion.identity;
            public Vector3 LeftMove;
            public HashSet<string> Hold;
            public HashSet<string> LeftGroup;
        }

        public string ShoulderShifts => string.Join(", ", _sides.Select(s => $"{s.E} {s.Shift.magnitude * 100f:F1} cm"));

        private Side MakeSide(string e, string b)
        {
            string P(string part) => $"Base Human{e}{part}";
            var s = new Side
            {
                E = e, B = b,
                Collar = P("Collarbone"), Upper = P("Upperarm"), Fore1 = P("Forearm1"), Fore2 = P("Forearm2"),
                Fore3 = P("Forearm3"), Palm = P("Palm"),
            };
            Vector3 u = _restPos[s.Upper], f = _restPos[s.Fore1], p = _restPos[s.Palm];
            s.L1 = (f - u).magnitude;
            s.L2 = (p - f).magnitude;
            Vector3 n = Vector3.Cross(f - u, p - f).normalized;
            s.QUpper = Quaternion.Inverse(_restRot[s.Upper]) * Quaternion.LookRotation(f - u, n);
            s.QFore1 = Quaternion.Inverse(_restRot[s.Fore1]) * Quaternion.LookRotation(p - f, n);
            s.RestPole = (f - (u + p) * 0.5f).normalized;
            s.QHand = Quaternion.Inverse(_restRot[s.Palm]) * HandFrame(_restPos[s.Palm], _restPos[P("Digit31")], _restPos[P("Digit21")], _restPos[P("Digit51")]);

            s.Fingers = new Finger[5];
            for (int k = 0; k < 5; k++)
            {
                var fg = new Finger();
                for (int j = 0; j < 3; j++)
                {
                    fg.Eft[j] = P($"Digit{k + 1}{j + 1}");
                    fg.Src[j] = $"j_{BoFingers[k]}_{b}_{j + 1}";
                }
                Vector3 up = HandFrame(_restPos[s.Palm], _restPos[P("Digit31")], _restPos[P("Digit21")], _restPos[P("Digit51")]) * Vector3.up;
                for (int j = 0; j < 2; j++)
                {
                    Vector3 dir = _restPos[fg.Eft[j + 1]] - _restPos[fg.Eft[j]];
                    fg.Qe[j] = Quaternion.Inverse(_restRot[fg.Eft[j]]) * Quaternion.LookRotation(dir, up);
                }
                int b2 = _bind.BoneIndex(fg.Src[1]), b3 = _bind.BoneIndex(fg.Src[2]);
                fg.Bind23 = Quaternion.Inverse(RgSpace.Rot(_bind.Bones[b2].Rest)) * RgSpace.Rot(_bind.Bones[b3].Rest);
                s.Fingers[k] = fg;
            }
            return s;
        }

        /// <summary>Anatomical hand frame: forward to the middle knuckle, up = palm normal from the index/pinky line.</summary>
        private static Quaternion HandFrame(Vector3 wrist, Vector3 middle, Vector3 index, Vector3 pinky)
        {
            Vector3 forward = middle - wrist;
            Vector3 up = Vector3.Cross(forward, index - pinky);
            return Quaternion.LookRotation(forward, up);
        }

        // ---------- One frame ----------

        /// <summary>Local transforms of every animated transform (path relative to the rig root) for one the original frame.</summary>
        /// <param name="kickFrom">
        /// When set, the clip's own gun motion is taken out and replaced by <see cref="KickCurve"/>, counted from this
        /// frame: everything the original moves with the gun (hands, parts) follows the replacement.
        /// </param>
        /// <param name="toIdle">
        /// 0..1: how far this frame's whole viewmodel (gun, hands, parts together) is moved onto the idle pose. Used to
        /// ease animations that start or end away from the idle into and out of it.
        /// </param>
        /// <param name="avoidKnob">Keep the left hand clear of the side dial (the original's first raise brushes through it).</param>
        /// <param name="inspect">0..1 through the built inspect (<see cref="InspectPose"/>), or below 0 for none.</param>
        public Dictionary<string, (Vector3 pos, Quaternion rot)> Solve(SrcAnim anim, int frame, List<string> warnings, int kickFrom = -1, float toIdle = 0f,
            bool avoidKnob = false, float inspect = -1f, Extras ex = null)
        {
            SrcPose[] f = anim.Frames[frame];
            SrcPose Part(string part)
            {
                int i = anim.PartIndex(part);
                return i >= 0 ? f[i] : _idle.Frames[0][Index(_idle, part)];
            }

            // Both corrections move the frame's viewmodel rigidly about the gun: kickRot turns it, and the gun's
            // origin goes from kickFromPos to kickToPos
            Quaternion kickRot = Quaternion.identity;
            Vector3 kickFromPos = Vector3.zero, kickToPos = Vector3.zero;
            bool corrected = kickFrom >= 0 || toIdle > 0f;
            float kick = 0f, rumble = 0f;
            if (corrected)
            {
                // Measured from the idle pose, not the shot's own first frame: the original starts the shot a little higher,
                // and the snap up to it and back read as the gun clipping upwards
                SrcPose g0 = _idle.Frames[0][Index(_idle, "tag_weapon")], gt = Part("tag_weapon");
                Quaternion r0 = RgSpace.Rot(g0);
                float w = kickFrom >= 0 ? 1f : toIdle;
                kickRot = Quaternion.Slerp(Quaternion.identity, r0 * Quaternion.Inverse(RgSpace.Rot(gt)), w);
                kickFromPos = RgSpace.Pos(gt.Pos) + _offset;
                kickToPos = Vector3.Lerp(kickFromPos, RgSpace.Pos(g0.Pos) + _offset, w);
                if (kickFrom >= 0)
                {
                    // The shot: no the original rock, only a push straight back along the bore, and a short rumble through it
                    float t = (frame - kickFrom) / (anim.FrameRate > 0 ? anim.FrameRate : 30f);
                    kick = KickCurve(t);
                    rumble = Rumble(t);
                    kickToPos -= (r0 * Vector3.right) * (KickBack * kick);
                    kickRot = Quaternion.AngleAxis(GunRumbleDegrees * rumble, r0 * Vector3.right) * kickRot;
                }
            }
            Vector3 BasePos(string part)
            {
                Vector3 p = RgSpace.Pos(Part(part).Pos) + _offset;
                return corrected ? kickToPos + kickRot * (p - kickFromPos) : p;
            }
            Quaternion BaseRot(string part) => kickRot * RgSpace.Rot(Part(part));

            // The left hand pushed out sideways from the gun while it's near the dial, so it never sinks into it
            Vector3 leftShift = Vector3.zero;
            if (avoidKnob)
            {
                Vector3 gun0 = BasePos("tag_weapon"), knob = BasePos(KnobTag);
                Vector3 bore = (BasePos("tag_flash") - gun0).normalized;
                Vector3 side = Vector3.ProjectOnPlane(knob - gun0, bore).normalized;
                float d = Mathf.Min(Vector3.Distance(BasePos("j_wrist_le"), knob), Vector3.Distance(BasePos("j_mid_le_1"), knob),
                    Vector3.Distance(BasePos("j_index_le_2"), knob));
                leftShift = side * (KnobClearance * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.04f, 0.11f, d))));
            }

            // The built inspect turns the whole viewmodel (gun and hands together) about the right wrist
            Quaternion insRot = Quaternion.identity;
            Vector3 insMove = Vector3.zero, insPivot = Vector3.zero;
            if (inspect >= 0f)
            {
                (insRot, insMove) = InspectPose(inspect);
                // Turned in the right hand, like a wrist turn: the right arm keeps its reach
                insPivot = BasePos("j_wrist_ri");
            }

            // The left hand's group (the hand, and a part it carries) turned about the wrist
            Vector3 leftWrist = ex != null ? BasePos("j_wrist_le") : Vector3.zero;
            bool InLeft(string part) => part.EndsWith("_le") || (ex?.LeftGroup != null && ex.LeftGroup.Contains(part));

            Vector3 BoPos(string part)
            {
                Vector3 p = BasePos(part);
                if (ex != null && InLeft(part))
                    p = leftWrist + ex.LeftTwist * (p - leftWrist) + ex.LeftMove;
                if (part.EndsWith("_le"))
                    p += leftShift;
                p = inspect >= 0f ? insPivot + insRot * (p - insPivot) + insMove : p;
                return ex != null ? p + ex.Push : p;
            }
            Quaternion BoRot(string part) => insRot * (ex != null && InLeft(part) ? ex.LeftTwist * BaseRot(part) : BaseRot(part));

            var world = new Dictionary<string, Quaternion>();
            var result = new Dictionary<string, (Vector3, Quaternion)>();

            // Camera: the original's camera motion relative to idle, on top of the rig's camera
            // Not during the shot: the original's camera jumps up with it, which read as the gun kicking up. The shot gets a
            // small jolt of its own instead, a slight pitch and push back, so it still feels forceful.
            Quaternion camDelta = kickFrom >= 0
                ? Quaternion.AngleAxis(CameraRumbleDegrees * rumble, Vector3.forward) * Quaternion.AngleAxis(-CameraKickDegrees * kick, Vector3.right)
                : RgSpace.Rot(Part("tag_camera")) * Quaternion.Inverse(RgSpace.Rot(_cameraRef));
            Vector3 camMove = kickFrom >= 0
                ? Vector3.back * (CameraKickBack * kick)
                : RgSpace.Pos(Part("tag_camera").Pos) - RgSpace.Pos(_cameraRef.Pos);
            SetWorld(result, "Camera_animated", _restPos["Camera_animated"] + camMove, camDelta * _restRot["Camera_animated"], _root);

            // Gun: tag_weapon is the gun model's root; the EFT weapon bone sits one fixed rotation above it.
            // EFT's procedural animation rewrites Weapon_root_anim every frame (hand position, sway, recoil), so it
            // stays at rest and the gun moves through the weapon bone below it
            // GunShift moves the item alone (the hands keep their own path): a bottle a little out of the grip
            Vector3 gunPos = BoPos("tag_weapon") + GunShift;
            Quaternion gunRot = BoRot("tag_weapon");
            Quaternion weaponRot = gunRot * Quaternion.Inverse(_jgunLocal);
            Transform rootAnim = _rig["Weapon_root_anim"];
            result[RgRig.PathOf(rootAnim, _root)] = (_restLocalPos["Weapon_root_anim"], _restLocal["Weapon_root_anim"]);
            Quaternion rootAnimRot = _restRot["Weapon_root_anim"];
            result[RgRig.PathOf(_rig["weapon"], _root)] =
                (Quaternion.Inverse(rootAnimRot) * (gunPos - _restPos["Weapon_root_anim"]), Quaternion.Inverse(rootAnimRot) * weaponRot);

            // Moving parts, relative to their parent bone in the gun model
            var gunWorld = new Dictionary<string, (Vector3 p, Quaternion r)> { [_gun.Bones[0].Name] = (gunPos, gunRot) };
            foreach (SrcBone bone in _gun.Bones.Skip(1))
            {
                string parent = _gun.Bones[bone.Parent].Name;
                (Vector3 pp, Quaternion pr) = gunWorld[parent];
                (Vector3 p, Quaternion r) pose;
                if (anim.PartIndex(bone.Name) >= 0 && (ex?.Hold == null || !ex.Hold.Contains(bone.Name)))
                    pose = (BoPos(bone.Name), BoRot(bone.Name));
                else
                {
                    Transform t = _gunBones[bone.Name];
                    pose = (pp + pr * t.localPosition, pr * t.localRotation);
                }
                gunWorld[bone.Name] = pose;
                result[RgRig.PathOf(_gunBones[bone.Name], _root)] = (Quaternion.Inverse(pr) * (pose.p - pp), Quaternion.Inverse(pr) * pose.r);
            }

            foreach (Side s in _sides)
                SolveArm(s, anim, f, BoPos, BoRot, world, result, warnings, frame, gunPos, weaponRot);
            return result;
        }

        private void SolveArm(Side s, SrcAnim anim, SrcPose[] f, Func<string, Vector3> boPos, Func<string, Quaternion> srcRot,
            Dictionary<string, Quaternion> world, Dictionary<string, (Vector3, Quaternion)> result, List<string> warnings, int frame,
            Vector3 weaponPos, Quaternion weaponRot)
        {
            Vector3 S = _restPos[s.Upper] + s.Shift;
            Vector3 W = boPos($"j_wrist_{s.B}");
            Transform collar = _rig[s.Collar];
            result[RgRig.PathOf(collar, _root)] = (collar.parent.InverseTransformPoint(_root.TransformPoint(_restPos[s.Collar] + s.Shift)), _restLocal[s.Collar]);
            Vector3 pole = boPos($"j_elbow_{s.B}") - S;

            // Two-bone IK: elbow on the plane through shoulder, wrist and the original's elbow
            Vector3 d = W - S;
            float dist = d.magnitude;
            float reach = s.L1 + s.L2 - 1e-4f;
            if (dist > reach && warnings.Count < 40)
                warnings.Add($"{s.E} hand {(dist - reach) * 100f:F1} cm out of reach at frame {frame}");
            dist = Mathf.Clamp(dist, Mathf.Abs(s.L1 - s.L2) + 1e-4f, reach);
            Vector3 dir = d.normalized;
            float a = (s.L1 * s.L1 - s.L2 * s.L2 + dist * dist) / (2f * dist);
            float h = Mathf.Sqrt(Mathf.Max(0f, s.L1 * s.L1 - a * a));
            pole -= dir * Vector3.Dot(pole, dir);
            pole = pole.sqrMagnitude > 1e-8f ? pole.normalized : s.RestPole;
            Vector3 E = S + dir * a + pole * h;
            Vector3 Wr = S + dir * dist;
            Vector3 n = Vector3.Cross(E - S, Wr - E);
            n = n.sqrMagnitude > 1e-10f ? n.normalized : Vector3.Cross(dir, pole).normalized;

            Quaternion rUpper = Quaternion.LookRotation(E - S, n) * Quaternion.Inverse(s.QUpper);
            Quaternion rFore1 = Quaternion.LookRotation(Wr - E, n) * Quaternion.Inverse(s.QFore1);
            Quaternion rFore2 = rFore1 * _restLocal[s.Fore2];
            Quaternion rFore3 = rFore2 * _restLocal[s.Fore3];

            // Palm: the original's hand frame, expressed in EFT's palm axes
            Vector3 bw = boPos($"j_wrist_{s.B}");
            Quaternion boHand = HandFrame(bw, boPos($"j_mid_{s.B}_1"), boPos($"j_index_{s.B}_1"), boPos($"j_pinky_{s.B}_1"));
            Quaternion rPalm = boHand * Quaternion.Inverse(s.QHand);

            // Forearm twist: whatever the palm turns about the forearm beyond the rigid chain, shared over Forearm2/3
            Vector3 axis = (Wr - E).normalized;
            Quaternion twist = RgSpace.Twist(rPalm * Quaternion.Inverse(rFore3 * _restLocal[s.Palm]), axis);
            rFore2 = Quaternion.Slerp(Quaternion.identity, twist, 1f / 3f) * rFore2;
            rFore3 = Quaternion.Slerp(Quaternion.identity, twist, 2f / 3f) * (rFore1 * _restLocal[s.Fore2] * _restLocal[s.Fore3]);

            Quaternion rCollar = _restRot[s.Collar];
            SetLocal(result, s.Upper, rCollar, rUpper);
            SetLocal(result, s.Fore1, rUpper, rFore1);
            SetLocal(result, s.Fore2, rFore1, rFore2);
            SetLocal(result, s.Fore3, rFore2, rFore3);
            SetLocal(result, s.Palm, rFore3, rPalm);
            world[s.Palm] = rPalm;

            // EFT's hand IK pulls each palm onto its marker on the gun and bends the elbow towards its goal. The
            // markers ride the animation (as in vanilla reloads), so the hands go where the original's go: the left one
            // carries the fresh cell in instead of staying on the grip
            Quaternion toWeapon = Quaternion.Inverse(weaponRot);
            foreach (string marker in new[] { $"weapon_{s.E}_IK_marker", $"weapon_{s.E}_hand_marker" })
            {
                if (_rig.TryGetValue(marker, out Transform m))
                    result[RgRig.PathOf(m, _root)] = (toWeapon * (Wr - weaponPos), toWeapon * rPalm);
            }
            if (_rig.TryGetValue($"Bend_Goal_{(s.E == "L" ? "Left" : "Right")}", out Transform goal))
            {
                Vector3 g = E + (E - (S + Wr) * 0.5f).normalized * 0.3f;
                result[RgRig.PathOf(goal, _root)] = (toWeapon * (g - weaponPos), goal.localRotation);
            }

            Vector3 handUp = boHand * Vector3.up;
            foreach (Finger fg in s.Fingers)
            {
                Quaternion parent = rPalm;
                Quaternion r2 = Quaternion.identity;
                for (int j = 0; j < 2; j++)
                {
                    Vector3 fdir = boPos(fg.Src[j + 1]) - boPos(fg.Src[j]);
                    Quaternion r = Quaternion.LookRotation(fdir, handUp) * Quaternion.Inverse(fg.Qe[j]);
                    SetLocal(result, fg.Eft[j], parent, r);
                    parent = r;
                    r2 = r;
                }
                // Last joint: no fingertip bone on either rig, so carry over the original's bend relative to its bind pose
                Quaternion rb2 = srcRot(fg.Src[1]), rb3 = srcRot(fg.Src[2]);
                Quaternion delta = rb3 * Quaternion.Inverse(rb2 * fg.Bind23);
                Quaternion r3 = delta * (r2 * _restLocal[fg.Eft[2]]);
                SetLocal(result, fg.Eft[2], r2, r3);
            }
        }

        private void SetLocal(Dictionary<string, (Vector3, Quaternion)> result, string bone, Quaternion parentWorld, Quaternion world)
        {
            Transform t = _rig[bone];
            result[RgRig.PathOf(t, _root)] = (t.localPosition, Quaternion.Inverse(parentWorld) * world);
        }

        private void SetWorld(Dictionary<string, (Vector3, Quaternion)> result, string bone, Vector3 pos, Quaternion rot, Transform parentSpace)
        {
            Transform t = _rig[bone];
            // Camera_animated hangs off the rig root
            result[RgRig.PathOf(t, _root)] = (pos, rot);
        }

        private static int Index(SrcAnim anim, string part)
        {
            int i = anim.PartIndex(part);
            if (i < 0)
                throw new InvalidOperationException($"Source animation is missing part {part}");
            return i;
        }

        // ---------- Clips ----------

        /// <summary>
        /// Bakes frames [first, last] of a the original animation into a clip with full position + rotation curves on every
        /// animated transform, so switching states always restores the whole pose.
        /// </summary>
        /// <param name="easeIn">Frames over which the clip starts from the exact idle pose (it begins elsewhere).</param>
        /// <param name="easeOut">Frames over which the clip ends on the exact idle pose (the state after it is the idle).</param>
        public AnimationClip BakeClip(string name, SrcAnim anim, int first, int last, bool loop, List<string> warnings, bool kick = false,
            int easeIn = 0, int easeOut = 0, bool avoidKnob = false, bool inspect = false, Func<int, Extras> extras = null)
        {
            first = Mathf.Clamp(first, 0, anim.Frames.Count - 1);
            last = Mathf.Clamp(last, first, anim.Frames.Count - 1);
            float fps = anim.FrameRate > 0 ? anim.FrameRate : 30f;
            var keys = new Dictionary<string, List<(float t, Vector3 p, Quaternion r)>>();
            for (int i = first; i <= last; i++)
            {
                float time = (i - first) / fps;
                float toIdle = Mathf.Max(
                    easeIn > 0 ? 1f - (i - first) / (float)easeIn : 0f,
                    easeOut > 0 ? 1f - (last - i) / (float)easeOut : 0f);
                toIdle = Mathf.Clamp01(toIdle);
                toIdle = toIdle * toIdle * (3f - 2f * toIdle);
                float ins = inspect ? (i - first) / (float)Mathf.Max(1, last - first) : -1f;
                foreach (var (path, (p, r)) in Solve(anim, i, warnings, kick ? first : -1, toIdle, avoidKnob, ins, extras?.Invoke(i - first)))
                {
                    if (!keys.TryGetValue(path, out var list))
                        keys[path] = list = new List<(float, Vector3, Quaternion)>();
                    list.Add((time, p, r));
                }
            }

            var clip = new AnimationClip { name = name, frameRate = fps, legacy = false };
            float length = Mathf.Max(1, last - first) / fps;
            foreach (var (path, list) in keys)
            {
                // A one-frame segment still needs a span for EFT's normalized event times
                if (list.Count == 1)
                    list.Add((length, list[0].p, list[0].r));
                // Keep quaternions on one hemisphere so curves don't spin the long way round
                for (int i = 1; i < list.Count; i++)
                {
                    if (Quaternion.Dot(list[i - 1].r, list[i].r) < 0f)
                    {
                        Quaternion q = list[i].r;
                        list[i] = (list[i].t, list[i].p, new Quaternion(-q.x, -q.y, -q.z, -q.w));
                    }
                }
                SetCurve(clip, path, "m_LocalPosition.x", list.Select(k => (k.t, k.p.x)));
                SetCurve(clip, path, "m_LocalPosition.y", list.Select(k => (k.t, k.p.y)));
                SetCurve(clip, path, "m_LocalPosition.z", list.Select(k => (k.t, k.p.z)));
                SetCurve(clip, path, "m_LocalRotation.x", list.Select(k => (k.t, k.r.x)));
                SetCurve(clip, path, "m_LocalRotation.y", list.Select(k => (k.t, k.r.y)));
                SetCurve(clip, path, "m_LocalRotation.z", list.Select(k => (k.t, k.r.z)));
                SetCurve(clip, path, "m_LocalRotation.w", list.Select(k => (k.t, k.r.w)));
            }
            clip.EnsureQuaternionContinuity();
            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = loop;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            return clip;
        }

        private static void SetCurve(AnimationClip clip, string path, string property, IEnumerable<(float t, float v)> keys)
        {
            var curve = new AnimationCurve(keys.Select(k => new Keyframe(k.t, k.v)).ToArray());
            for (int i = 0; i < curve.length; i++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
            }
            clip.SetCurve(path, typeof(Transform), property, curve);
        }

        // ---------- Static placements ----------

        /// <summary>
        /// Places the weapon's markers from a solved frame: EFT's hand IK pulls the palms onto weapon_*_IK_marker,
        /// bends the elbows towards Bend_Goal_*, and aims with aim_camera.
        /// </summary>
        public void PlaceMarkers(SrcAnim idle, SrcAnim adsUp)
        {
            // Pose the rig at idle so world positions can be read back
            ApplyFrame(idle, 0);
            Transform weapon = _rig["weapon"];
            foreach (string e in new[] { "L", "R" })
            {
                Transform palm = _rig[$"Base Human{e}Palm"];
                foreach (string marker in new[] { $"weapon_{e}_IK_marker", $"weapon_{e}_hand_marker" })
                {
                    if (_rig.TryGetValue(marker, out Transform m))
                    {
                        m.position = palm.position;
                        m.rotation = palm.rotation;
                    }
                }
                Transform elbow = _rig[$"Base Human{e}Forearm1"];
                Transform upper = _rig[$"Base Human{e}Upperarm"];
                if (_rig.TryGetValue($"Bend_Goal_{(e == "L" ? "Left" : "Right")}", out Transform goal))
                    goal.position = elbow.position + (elbow.position - (upper.position + palm.position) * 0.5f).normalized * 0.3f;
                if (_rig.TryGetValue($"weapon_{e}Collarbone_marker", out Transform collar))
                {
                    collar.position = _rig[$"Base Human{e}Collarbone"].position;
                    collar.rotation = _rig[$"Base Human{e}Collarbone"].rotation;
                }
            }

            // Muzzle and ejection points from the gun's own tags
            PlaceAt("fireport", "tag_flash");
            PlaceAt("smokeport", "tag_flash");
            PlaceAt("shellport", "tag_brass");
            PlaceAt("patron_in_weapon", "tag_brass");

            // ADS: the original aims through engine view offsets, not the viewmodel, so place the eye over the bore, just above
            // the top of the gun and a hand's width ahead of the grip (EFT lines aim_camera up with the camera)
            if (_rig.TryGetValue("aim_camera", out Transform aim) && _gunBones.TryGetValue("tag_flash", out Transform flash))
            {
                Bounds top = TopBounds(weapon);
                Vector3 muzzle = weapon.InverseTransformPoint(flash.position);
                Vector3 grip = weapon.InverseTransformPoint(_gunBones[_gun.Bones[0].Name].position);
                Quaternion keep = aim.localRotation;
                // EFT weapon space: -Y forward, +Z up
                aim.localPosition = new Vector3(muzzle.x, grip.y - 0.12f, top.max.z + 0.035f);
                aim.localRotation = keep;
            }
            ApplyFrame(idle, 0);
        }

        /// <summary>The gun's mesh bounds in the weapon bone's space.</summary>
        private Bounds TopBounds(Transform weapon)
        {
            var bounds = new Bounds(Vector3.zero, Vector3.zero);
            bool first = true;
            foreach (MeshFilter mf in _gunBones[_gun.Bones[0].Name].GetComponentsInChildren<MeshFilter>())
            {
                foreach (Vector3 v in mf.sharedMesh.vertices)
                {
                    Vector3 p = weapon.InverseTransformPoint(mf.transform.TransformPoint(v));
                    if (first) { bounds = new Bounds(p, Vector3.zero); first = false; }
                    else bounds.Encapsulate(p);
                }
            }
            return bounds;
        }

        private void PlaceAt(string slot, string gunTag)
        {
            if (_rig.TryGetValue(slot, out Transform t) && _gunBones.TryGetValue(gunTag, out Transform tag))
            {
                Quaternion keep = t.localRotation;
                t.position = tag.position;
                t.localRotation = keep; // keep EFT's orientation convention for the slot
            }
        }

        /// <summary>Applies a solved frame to the live rig (for marker placement and previews).</summary>
        public void ApplyFrame(SrcAnim anim, int frame)
        {
            var warnings = new List<string>();
            foreach (var (path, (p, r)) in Solve(anim, frame, warnings))
            {
                Transform t = _root.Find(path);
                if (t == null)
                    continue;
                t.localPosition = p;
                t.localRotation = r;
            }
        }
    }
}

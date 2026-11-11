using System;
using EFT;
using EFT.CameraControl;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The map editor's free camera (no other mod needed): the player's body stays where it was and stops taking input
    /// (its GamePlayerOwner off), EFT's camera stops following it (its PlayerCameraController off), and the camera flies.
    /// WASD fly as you look, Q / E down / up, Shift faster, hold the right mouse button to look about. The cursor stays
    /// free for the editor's window.
    /// </summary>
    internal class FreeCam
    {
        public bool Active { get; private set; }
        public float Speed = 6f;

        private Player _player;
        private GamePlayerOwner _owner;
        private PlayerCameraController _follow;
        private Transform _camera;
        private Vector3 _position;
        private float _yaw, _pitch;
        private Vector3 _lastMouse;
        private bool _axesWork = true;

        public Camera Camera => _camera != null ? _camera.GetComponent<Camera>() : null;

        public bool Start(Player player)
        {
            Camera cam = ZombiesInteraction.ActiveCamera;
            if (Active || player == null || cam == null)
                return false;
            _player = player;
            _owner = ((Component)player).GetComponent<GamePlayerOwner>();
            _follow = ((Component)player).GetComponent<PlayerCameraController>();
            if (_owner != null)
                _owner.enabled = false;
            if (_follow != null)
                _follow.enabled = false;
            _camera = cam.transform;
            _position = _camera.position;
            Vector3 e = _camera.rotation.eulerAngles;
            _yaw = e.y;
            _pitch = e.x > 180f ? e.x - 360f : e.x;
            Active = true;
            Application.onBeforeRender += Pose;
            return true;
        }

        public void Stop()
        {
            if (!Active)
                return;
            Active = false;
            Application.onBeforeRender -= Pose;
            if (_owner != null)
                _owner.enabled = true;
            if (_follow != null)
                _follow.enabled = true;
            _owner = null;
            _follow = null;
            _player = null;
        }

        /// <summary>Moves the camera from the keys and mouse (call every frame while active).</summary>
        public void Tick(bool keysFree)
        {
            if (!Active)
                return;
            if (_player == null || _camera == null)
            {
                Stop();
                return;
            }
            bool look = Input.GetMouseButton(1);
            if (look)
            {
                Vector2 d = MouseDelta();
                _yaw += d.x;
                _pitch = Mathf.Clamp(_pitch - d.y, -89f, 89f);
            }
            else
                _lastMouse = Input.mousePosition;

            if (keysFree)
            {
                Quaternion turn = Quaternion.Euler(_pitch, _yaw, 0f);
                Vector3 move = Vector3.zero;
                if (Input.GetKey(KeyCode.W)) move += turn * Vector3.forward;
                if (Input.GetKey(KeyCode.S)) move -= turn * Vector3.forward;
                if (Input.GetKey(KeyCode.D)) move += turn * Vector3.right;
                if (Input.GetKey(KeyCode.A)) move -= turn * Vector3.right;
                if (Input.GetKey(KeyCode.E)) move += Vector3.up;
                if (Input.GetKey(KeyCode.Q)) move -= Vector3.up;
                float speed = Speed * (Input.GetKey(KeyCode.LeftShift) ? 4f : 1f);
                _position += move * (speed * Time.unscaledDeltaTime);
            }
            Pose();
        }

        /// <summary>Where the camera is and which way it looks on the ground (for placing in front of it).</summary>
        public Vector3 Position => _position;
        public Vector3 FlatForward => Quaternion.Euler(0f, _yaw, 0f) * Vector3.forward;

        public void MoveTo(Vector3 position) => _position = position;

        // Set again just before drawing, after anything else that moved it this frame
        private void Pose()
        {
            if (Active && _camera != null)
                _camera.SetPositionAndRotation(_position, Quaternion.Euler(_pitch, _yaw, 0f));
        }

        /// <summary>The mouse's movement this frame in degrees: Unity's mouse axes, or the cursor's travel if EFT has none.</summary>
        private Vector2 MouseDelta()
        {
            if (_axesWork)
            {
                try
                {
                    return new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * 2f;
                }
                catch (ArgumentException)
                {
                    _axesWork = false;
                }
            }
            Vector3 now = Input.mousePosition;
            Vector3 d = now - _lastMouse;
            _lastMouse = now;
            return new Vector2(d.x, d.y) * 0.15f;
        }
    }
}

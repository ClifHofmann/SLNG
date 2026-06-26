using Godot;
using System;

namespace SLNG.App
{
    public partial class FreeCamera : Camera3D
    {
        [Export] public float BaseSpeed = 10.0f;
        [Export] public float ShiftMultiplier = 5.0f;
        [Export] public float MouseSensitivity = 0.002f;

        private Vector2 _mouseDelta = Vector2.Zero;
        private float _yaw = 0.0f;
        private float _pitch = 0.0f;

        public override void _Ready()
        {
            // Initial rotation based on camera's current transform
            Vector3 euler = Rotation;
            _pitch = euler.X;
            _yaw = euler.Y;
        }

        public override void _Input(InputEvent @event)
        {
            if (@event is InputEventMouseButton mouseButtonEvent)
            {
                if (mouseButtonEvent.ButtonIndex == MouseButton.Right)
                {
                    if (mouseButtonEvent.Pressed)
                    {
                        Input.MouseMode = Input.MouseModeEnum.Captured;
                    }
                    else
                    {
                        Input.MouseMode = Input.MouseModeEnum.Visible;
                    }
                }
            }
            else if (@event is InputEventMouseMotion mouseMotionEvent)
            {
                if (Input.MouseMode == Input.MouseModeEnum.Captured)
                {
                    _mouseDelta = mouseMotionEvent.Relative;

                    _yaw -= _mouseDelta.X * MouseSensitivity;
                    _pitch -= _mouseDelta.Y * MouseSensitivity;

                    // Clamp pitch to prevent flipping
                    _pitch = Mathf.Clamp(_pitch, -Mathf.Pi / 2.0f + 0.01f, Mathf.Pi / 2.0f - 0.01f);

                    Rotation = new Vector3(_pitch, _yaw, 0.0f);
                }
            }
        }

        public override void _Process(double delta)
        {
            Vector3 direction = Vector3.Zero;
            
            if (Input.IsKeyPressed(Key.W)) direction -= Transform.Basis.Z;
            if (Input.IsKeyPressed(Key.S)) direction += Transform.Basis.Z;
            if (Input.IsKeyPressed(Key.A)) direction -= Transform.Basis.X;
            if (Input.IsKeyPressed(Key.D)) direction += Transform.Basis.X;
            if (Input.IsKeyPressed(Key.E)) direction += Vector3.Up;
            if (Input.IsKeyPressed(Key.Q)) direction -= Vector3.Up;

            if (direction != Vector3.Zero)
            {
                direction = direction.Normalized();
                
                float speed = BaseSpeed;
                if (Input.IsKeyPressed(Key.Shift))
                {
                    speed *= ShiftMultiplier;
                }

                Position += direction * speed * (float)delta;
            }
        }
    }
}

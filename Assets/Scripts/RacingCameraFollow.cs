using UnityEngine;

namespace Assets.Scripts
{
    [DisallowMultipleComponent]
    public class RacingCameraFollow : MonoBehaviour
    {
        [Header("Target")]
        [SerializeField] private bool isActive = true;
        [SerializeField] private Transform target;

        [Header("Position")]
        [Tooltip("Camera offset relative to the target. If 'Use Target Yaw Only' is enabled, this offset is rotated by the target's Y rotation.")]
        [SerializeField] private Vector3 offset = new Vector3(0f, 3f, -7f);

        [Tooltip("Time (seconds) used by SmoothDamp for position.")]
        [SerializeField, Min(0f)] private float positionSmoothTime = 0.15f;

        [Tooltip("If enabled, the offset rotates with the target's Y (yaw) rotation, like a third-person racing camera.")]
        [SerializeField] private bool useTargetYawOnly = true;

        [Header("Rotation")]
        [Tooltip("If enabled, camera will look at the target (plus Look At Offset).")]
        [SerializeField] private bool lookAtTarget = true;

        [Tooltip("Offset applied to the look-at point.")]
        [SerializeField] private Vector3 lookAtOffset = new Vector3(0f, 1.0f, 0f);

        [Tooltip("Higher values rotate faster.")]
        [SerializeField, Min(0f)] private float rotationLerpSpeed = 12f;

        private Vector3 _positionVelocity;

        public Transform Target
        {
            get => target;
            set => target = value;
        }

        public bool IsActive
        {
            get => isActive;
            set => isActive = value;
        }

        void Reset()
        {
            var cameraTransform = transform;
            if (cameraTransform.parent != null)
                cameraTransform.SetParent(null);
        }

        void LateUpdate()
        {
            if (!isActive || target == null)
                return;

            Quaternion referenceRotation = useTargetYawOnly
                ? Quaternion.Euler(0f, target.eulerAngles.y, 0f)
                : target.rotation;

            Vector3 desiredPosition = target.position + (referenceRotation * offset);

            if (positionSmoothTime <= 0f)
            {
                transform.position = desiredPosition;
            }
            else
            {
                transform.position = Vector3.SmoothDamp(
                    transform.position,
                    desiredPosition,
                    ref _positionVelocity,
                    positionSmoothTime);
            }

            if (!lookAtTarget)
                return;

            Vector3 lookPoint = target.position + (referenceRotation * lookAtOffset);
            Vector3 toLookPoint = lookPoint - transform.position;
            if (toLookPoint.sqrMagnitude < 0.0001f)
                return;

            Quaternion desiredRotation = Quaternion.LookRotation(toLookPoint.normalized, Vector3.up);

            if (rotationLerpSpeed <= 0f)
            {
                transform.rotation = desiredRotation;
            }
            else
            {
                float t = 1f - Mathf.Exp(-rotationLerpSpeed * Time.deltaTime);
                transform.rotation = Quaternion.Slerp(transform.rotation, desiredRotation, t);
            }
        }
    }
}

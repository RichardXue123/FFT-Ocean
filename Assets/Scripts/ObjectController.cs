using System;
using UnityEngine;

namespace Assets.Scripts
{
    public class ObjectController : MonoBehaviour
    {
        public float moveAccel = 10f;  // 平移加速度（单位：m/s²）
        private Rigidbody rb;

        void Start()
        {
            rb = GetComponent<Rigidbody>();
        }

        void Update()
        {
            Vector3 accel = Vector3.zero;

            // 获取摄像机方向
            Transform cam = Camera.main.transform;

            // 水平 forward / right
            Vector3 forward = cam.forward;
            forward.y = 0;
            forward.Normalize();

            Vector3 right = cam.right;
            right.y = 0;
            right.Normalize();

            // 按键映射：IJKL = 前后左右
            if (Input.GetKey(KeyCode.I))
                accel += forward * moveAccel;
            if (Input.GetKey(KeyCode.K))
                accel -= forward * moveAccel;
            if (Input.GetKey(KeyCode.L))
                accel += right * moveAccel;
            if (Input.GetKey(KeyCode.J))
                accel -= right * moveAccel;

            // 空格键 - 向上
            if (Input.GetKey(KeyCode.Space))
                accel += Vector3.up * moveAccel;

            // 直接施加加速度（忽略质量）
            rb.AddForce(accel, ForceMode.Acceleration);
        }
    }
}

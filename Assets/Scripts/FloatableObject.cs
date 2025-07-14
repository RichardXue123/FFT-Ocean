using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Assets.Scripts
{
    public class FloatableObject : MonoBehaviour
    {
        public WaveParticleSystem water;
        [SerializeField] public int regionIndex;
        protected Rigidbody rb;
        public Vector3 bottomOffset = new Vector3(0, -0.5f, 0);

        protected virtual void Awake()
        {
            rb = GetComponent<Rigidbody>();
        }

        protected virtual void FixedUpdate()
        {
            Vector3 bottomWorld = transform.position + transform.rotation * bottomOffset;
            float waterHeight = water.QueryHeight(new Vector2(bottomWorld.x, bottomWorld.z));
            float depth = waterHeight - bottomWorld.y;

            if (depth > 0)
            {
                Vector3 force = Vector3.up * depth * 10f;
                rb.AddForceAtPosition(force, bottomWorld);
            }

            // 可扩展：检测下压/上浮
            HandleWaveParticleInteraction(bottomWorld, depth);
        }

        // 可被派生类复写以自定义交互
        protected virtual void HandleWaveParticleInteraction(Vector3 bottomWorld, float depth)
        {
            // 留空或添加基础波粒子生成逻辑
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Assets.Scripts
{
    public class ObjectController : MonoBehaviour
    {
        public float moveForce = 100000f;  // 平移力度

        private Rigidbody rb;

        void Start()
        {
            rb = GetComponent<Rigidbody>();  // 获取刚体组件
        }

        void Update()
        {
            Vector3 force = Vector3.zero;

            // J=左，L=右，I=前，K=后
            if (Input.GetKey(KeyCode.J))
                force.x -= moveForce;
            if (Input.GetKey(KeyCode.L))
                force.x += moveForce;
            if (Input.GetKey(KeyCode.I))
                force.z += moveForce;
            if (Input.GetKey(KeyCode.K))
                force.z -= moveForce;
            // 空格键 - 向上（y轴）加力
            if (Input.GetKey(KeyCode.Space))
                force.y += moveForce;
            rb.AddForce(force);
        }
    }
}

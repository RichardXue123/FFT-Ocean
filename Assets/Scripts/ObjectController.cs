using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class SimpleBoatController : MonoBehaviour
{
    [Header("Easy Params")]
    [Tooltip("前进最高速度 m/s")]
    public float maxSpeed = 10f;
    [Tooltip("倒车最高速度 m/s")]
    public float maxReverseSpeed = 5f;
    [Tooltip("最大加速度 m/s^2（加速用）")]
    public float accel = 4f;
    [Tooltip("最大减速度 m/s^2（松油/刹车用）")]
    public float brake = 6f;
    [Tooltip("满舵时的目标偏航角速度（度/秒）")]
    public float maxTurnRateDeg = 90f;
    [Tooltip("转向响应速度（角加速度），建议值 5-10")]
    public float turnResponse = 5f;
    [Tooltip("横向速度抑制（越大越不打滑）")]
    public float lateralGrip = 3f;

    [Header("Vertical Lift (Space)")]
    [Tooltip("按住空格时的垂直目标上升速度 m/s")]
    public float upMaxSpeed = 5f;
    [Tooltip("最大上升加速度 m/s^2（限制抬升的猛劲）")]
    public float upAccel = 12f;

    [Header("Smoothing")]
    [Tooltip("油门/舵输入的平滑时间常数（秒）")]
    public float inputSmooth = 0.12f;

    Rigidbody rb;
    float throttleSm, steerSm; // 平滑后的输入
    float liftSm;              // 空格抬升（0..1）平滑

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }

    void Update()
    {
        // I/K: 前进/后退；J/L: 左/右；Space: 向上抬
        float throttleRaw = (Input.GetKey(KeyCode.I) ? 1f : 0f) +
                            (Input.GetKey(KeyCode.K) ? -1f : 0f);
        float steerRaw = (Input.GetKey(KeyCode.L) ? 1f : 0f) +
                            (Input.GetKey(KeyCode.J) ? -1f : 0f);
        float liftRaw = Input.GetKey(KeyCode.Space) ? 1f : 0f;

        // 指数平滑（手感更顺）
        float a = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(1e-4f, inputSmooth));
        throttleSm = Mathf.Lerp(throttleSm, Mathf.Clamp(throttleRaw, -1f, 1f), a);
        steerSm = Mathf.Lerp(steerSm, Mathf.Clamp(steerRaw, -1f, 1f), a);
        liftSm = Mathf.Lerp(liftSm, Mathf.Clamp01(liftRaw), a);
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        if (dt <= 0f) return;

        // —— 1) 速度目标（船体局部 Z 轴方向） ——
        Vector3 vLs = transform.InverseTransformDirection(rb.velocity);
        float vz = vLs.z;

        float vTarget = (throttleSm >= 0f)
            ? throttleSm * maxSpeed
            : throttleSm * maxReverseSpeed;

        float dv = vTarget - vz;
        float aLimit = (dv >= 0f) ? accel : brake;
        float aCmd = Mathf.Clamp(dv / dt, -aLimit, aLimit);

        Vector3 fwdForce = transform.forward * (rb.mass * aCmd);
        rb.AddForce(fwdForce, ForceMode.Force);

        // 自然一点的速度上限（超速时轻微刹车）
        // 只限制水平速度，垂直自由下落
        Vector3 vHoriz = Vector3.ProjectOnPlane(rb.velocity, Vector3.up);
        float hspd = vHoriz.magnitude;
        float hMax = Mathf.Max(maxSpeed, maxReverseSpeed);

        if (hspd > hMax)
        {
            Vector3 brakeHoriz = -vHoriz.normalized * (rb.mass * (hspd - hMax) / dt * 0.2f);
            rb.AddForce(brakeHoriz, ForceMode.Force);
        }


        // —— 2) 侧滑抑制：把局部 X 速度往 0 拉（越大越稳） ——
        float vx = vLs.x;
        Vector3 latForce = -transform.right * (rb.mass * vx * lateralGrip);
        rb.AddForce(latForce, ForceMode.Force);

        // —— 3) 转向：目标偏航角速度（deg/s → rad/s），P 控制器到目标 ——
        float yawRateTarget = steerSm * maxTurnRateDeg * Mathf.Deg2Rad;
        float yawRate = rb.angularVelocity.y;
        float yawErr = yawRateTarget - yawRate;
        
        // 使用 ForceMode.Acceleration，忽略质量影响
        // 纯 P 控制器：角加速度 = k * (目标速度 - 当前速度)
        // 这样无论质量多大，转向响应都一致
        float alphaY = turnResponse * yawErr;
        rb.AddTorque(Vector3.up * alphaY, ForceMode.Acceleration);

        // —— 4) 空格抬升：把竖直速度拉到 upMaxSpeed（受 upAccel 限制） ——
        if (liftSm > 0.001f)
        {
            float vy = rb.velocity.y;
            float vTargetY = liftSm * upMaxSpeed;              // 0..upMaxSpeed
            float dvY = vTargetY - vy;
            float aY = Mathf.Clamp(dvY / dt, 0f, upAccel);     // 只给向上的加速度
            rb.AddForce(Vector3.up * (rb.mass * aY), ForceMode.Force);
        }
        // 松开空格不额外下压，交给重力/浮力系统自然回落
    }
}

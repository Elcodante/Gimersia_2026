using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using System.Collections.Generic;

public class CameraTracker : MonoBehaviour
{
    private CinemachineTargetGroup targetGroup;
    [SerializeField] private float playerRadius = 1.5f;

    private void Start()
    {
        targetGroup = GetComponent<CinemachineTargetGroup>();
        NetworkManager.Singleton.OnClientConnectedCallback += (id) => Invoke(nameof(RefreshTargets), 0.5f);
        NetworkManager.Singleton.OnClientDisconnectCallback += (id) => Invoke(nameof(RefreshTargets), 0.5f);
        Invoke(nameof(RefreshTargets), 0.5f);
    }

    private void RefreshTargets()
    {
        if (targetGroup == null) return;
        List<CinemachineTargetGroup.Target> newTargets = new List<CinemachineTargetGroup.Target>();
        GameObject[] players = GameObject.FindGameObjectsWithTag("Player");

        foreach (GameObject player in players)
        {
            newTargets.Add(new CinemachineTargetGroup.Target { Object = player.transform, Weight = 1f, Radius = playerRadius });
        }

        targetGroup.Targets = newTargets;

        // Jika ternyata belum ada satupun pemain yang spawn, periksa lagi setengah detik kemudian!
        if (players.Length == 0)
        {
            Invoke(nameof(RefreshTargets), 0.5f);
        }
    }
}
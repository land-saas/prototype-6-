using UnityEngine;

public class Win_portal : MonoBehaviour
{
    //player win when they collide with this portal
    public GameObject WIN;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    void OnTriggerEnter2D(Collider2D collide)
    {
        if(collide.gameObject.CompareTag("Player"))
        {
            //gameover
            WIN.SetActive(true);
        }
    }

    void OnTriggerExit2D(Collider2D collide)
    {
        if(collide.gameObject.CompareTag("Player"))
        {
            //gameover
            WIN.SetActive(false);
        }
    }
}

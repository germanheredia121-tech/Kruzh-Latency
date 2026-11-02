import cloudscraper
import sys

def main():
    print("Testing cloudscraper on Blur Busters...")
    scraper = cloudscraper.create_scraper(browser={
        'browser': 'chrome',
        'platform': 'windows',
        'desktop': True
    })
    
    url = "https://forums.blurbusters.com/viewforum.php?f=10"
    try:
        resp = scraper.get(url, timeout=15)
        print(f"Status Code: {resp.status_code}")
        if resp.status_code == 200:
            with open("test_bb.html", "w", encoding="utf-8") as f:
                f.write(resp.text)
            print("Successfully saved test_bb.html")
        else:
            print("Failed to bypass Cloudflare or other error.")
    except Exception as e:
        print(f"Exception occurred: {e}")

if __name__ == "__main__":
    main()

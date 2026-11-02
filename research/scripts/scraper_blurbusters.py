import cloudscraper
from bs4 import BeautifulSoup
import json
import time
import random
import urllib.parse
import sys
import os

BASE_URL = "https://forums.blurbusters.com"
FORUM_ID = 10
START_URL = f"{BASE_URL}/viewforum.php?f={FORUM_ID}"

# ================= CONFIGURACION =================
# Cambia a "None" para scrapear TODOS los threads del foro.
# Por defecto lo dejo en 5 para que lo puedas probar rapido sin que tarde 10 horas.
MAX_THREADS_TO_SCRAPE = None
MAX_PAGES_PER_THREAD = 999 # Numero de paginas a leer por cada hilo
# =================================================

def random_delay():
    # Delay de 5 a 10 segundos para evadir deteccion anti-bot (DDoS protection)
    delay = random.uniform(5, 10)
    time.sleep(delay)

def get_soup(scraper, url):
    print(f"Obteniendo: {url}")
    try:
        resp = scraper.get(url, timeout=15)
        if resp.status_code == 200:
            return BeautifulSoup(resp.text, 'html.parser')
        else:
            print(f"Error HTTP {resp.status_code} al obtener {url}")
            return None
    except Exception as e:
        print(f"Excepcion al conectar con {url}: {e}")
        return None

def main():
    sys.stdout.reconfigure(encoding='utf-8')
    print("Inicializando Cloudscraper para evadir Cloudflare...")
    scraper = cloudscraper.create_scraper(browser={
        'browser': 'chrome',
        'platform': 'windows',
        'desktop': True
    })
    
    threads = []
    
    # PASO 1: Recolectar links de los threads navegando por las paginas del subforo
    print("Extrayendo lista de hilos (Threads)...")
    current_page_url = START_URL
    page_num = 1
    
    while current_page_url:
        print(f"  -> Leyendo pagina {page_num}...")
        soup = get_soup(scraper, current_page_url)
        if not soup:
            print("Fallo critico al acceder a la pagina del foro.")
            break
            
        for a in soup.select('a.topictitle'):
            href = a['href']
            parsed = urllib.parse.urlparse(href)
            query = urllib.parse.parse_qs(parsed.query)
            if 't' in query:
                thread_id = query['t'][0]
                thread_url = f"{BASE_URL}/viewtopic.php?f={FORUM_ID}&t={thread_id}"
                title = a.get_text(strip=True)
                if not any(t['id'] == thread_id for t in threads):
                    threads.append({'title': title, 'url': thread_url, 'id': thread_id})
                    
        print(f"  -> Hilos acumulados: {len(threads)}")
        
        if MAX_THREADS_TO_SCRAPE and len(threads) >= MAX_THREADS_TO_SCRAPE:
            threads = threads[:MAX_THREADS_TO_SCRAPE]
            break
            
        # Buscar el boton de "Siguiente pagina" (rel="next" en phpBB)
        next_button = soup.find('a', rel='next')
        if next_button and 'href' in next_button.attrs:
            next_href = next_button['href']
            # Remover el SID (Session ID) para evitar bloqueos 403
            parsed_next = urllib.parse.urlparse(next_href)
            query_next = urllib.parse.parse_qs(parsed_next.query)
            if 'sid' in query_next:
                del query_next['sid']
            # Reconstruir query sin SID
            new_query = urllib.parse.urlencode(query_next, doseq=True)
            clean_href = urllib.parse.urlunparse(parsed_next._replace(query=new_query))
            
            if clean_href.startswith('./'):
                current_page_url = f"{BASE_URL}{clean_href[1:]}"
            else:
                current_page_url = clean_href
            page_num += 1
            random_delay() # Pausa entre paginas del indice
        else:
            current_page_url = None
    
    database = []
    
    # PASO 2: Entrar a cada thread y extraer los posts (mensajes)
    for i, thread in enumerate(threads):
        print(f"\n[{i+1}/{len(threads)}] Analizando: {thread['title']}")
        random_delay()
        
        thread_soup = get_soup(scraper, thread['url'])
        if not thread_soup:
            continue
            
        posts = []
        # En phpBB3, los mensajes se estructuran en divs con la clase 'post'
        for post_div in thread_soup.select('div.post'):
            # El nombre de usuario puede tener color (rango) o no
            author_tag = post_div.select_one('.username') or post_div.select_one('.username-coloured')
            author = author_tag.get_text(strip=True) if author_tag else "Unknown"
            
            # El contenido en si del mensaje
            content_tag = post_div.select_one('.content')
            content = content_tag.get_text(separator='\n', strip=True) if content_tag else ""
            
            # Fecha del post (Suele estar en tag time o author span)
            date_tag = post_div.select_one('time')
            date_str = date_tag['datetime'] if date_tag and date_tag.has_attr('datetime') else "N/A"
            
            posts.append({
                'author': author,
                'date': date_str,
                'content': content
            })
            
        thread_data = {
            'thread_id': thread['id'],
            'title': thread['title'],
            'url': thread['url'],
            'posts_count': len(posts),
            'posts': posts
        }
        database.append(thread_data)
        
    # PASO 3: Guardar base de datos
    out_file = os.path.join(os.path.dirname(os.path.abspath(__file__)), "blurbusters_inputlag.json")
    with open(out_file, "w", encoding="utf-8") as f:
        json.dump(database, f, indent=4, ensure_ascii=False)
        
    print(f"\n==============================================")
    print(f"¡Scraping Finalizado!")
    print(f"Total de hilos analizados: {len(database)}")
    print(f"Archivo guardado en: {out_file}")
    print(f"==============================================")

if __name__ == "__main__":
    main()

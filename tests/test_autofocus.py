"""验证自动聚焦修复"""
import time
from playwright.sync_api import sync_playwright

def test_auto_focus():
    print("Testing auto-focus fix...")
    
    with sync_playwright() as p:
        browser = p.chromium.launch(channel="msedge", headless=True)
        context = browser.new_context(viewport={"width": 1920, "height": 1080})
        page = context.new_page()
        
        try:
            # 进入会话页面
            page.goto("http://localhost:5000/sessions")
            page.wait_for_load_state("networkidle")
            
            # 点击第一个会话
            session_card = page.query_selector('.session-card-wrapper, .session-card')
            if session_card:
                session_card.click()
                page.wait_for_load_state("networkidle")
                page.wait_for_timeout(2000)
                
                # 检查输入框是否聚焦
                focused_element = page.evaluate("document.activeElement.tagName")
                focused_id = page.evaluate("document.activeElement.id")
                focused_class = page.evaluate("document.activeElement.className")
                
                print(f"Focused element: {focused_element}")
                print(f"Focused element ID: {focused_id}")
                print(f"Focused element class: {focused_class}")
                
                # 检查是否是 textarea
                if focused_element.lower() == 'textarea':
                    print("\n✅ SUCCESS: 输入框已自动聚焦!")
                    return True
                else:
                    print("\n❌ FAILED: 输入框未自动聚焦")
                    return False
            else:
                print("No session found, creating new one...")
                new_btn = page.query_selector('button:has-text("新建会话")')
                if new_btn:
                    new_btn.click()
                    page.wait_for_load_state("networkidle")
                    page.wait_for_timeout(3000)
                    
                    focused_element = page.evaluate("document.activeElement.tagName")
                    if focused_element.lower() == 'textarea':
                        print("\n✅ SUCCESS: 输入框已自动聚焦!")
                        return True
                    else:
                        print(f"\n❌ FAILED: 聚焦元素是 {focused_element}")
                        return False
                    
        except Exception as e:
            print(f"Error: {e}")
            return False
        finally:
            browser.close()

if __name__ == "__main__":
    result = test_auto_focus()
    print(f"\n测试结果: {'通过' if result else '失败'}")